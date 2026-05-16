"""
NativeGameEnv + Batched Gumbel MCTS。
batched_mcts.py の _expand を shogi_expand（バッチ API）で高速化したバージョン。

1 シミュレーションあたりのネイティブ呼び出し:
  従来 (Python.NET):   apply×1 + to_tensor×1 + legal_moves×1 = 3 回 × 8ms = 24ms
  NativeAOT のみ:      apply×1 + to_tensor×1 + legal_moves×1 = 3 回 × 0.1ms = 0.3ms
  NativeAOT + batch:   apply×1 + expand×1(apply+tensor+moves) = 2 回 × 0.1ms = 0.2ms
"""

import math
import numpy as np
from typing import Optional

C_PUCT = 1.25


class Node:
    __slots__ = ('sfen', 'parent', 'move', 'prior',
                 'children', 'visit_count', 'value_sum',
                 'is_expanded', 'is_terminal', 'terminal_value',
                 'virtual_loss')

    def __init__(self, sfen=None, parent=None, move=None, prior=0.0):
        self.sfen           = sfen
        self.parent         = parent
        self.move           = move
        self.prior          = prior
        self.children: dict[int, 'Node'] = {}
        self.visit_count    = 0
        self.value_sum      = 0.0
        self.is_expanded    = False
        self.is_terminal    = False
        self.terminal_value = 0.0
        self.virtual_loss   = 0

    @property
    def q_value(self) -> float:
        n = self.visit_count + self.virtual_loss
        return (self.value_sum - self.virtual_loss) / n if n > 0 else 0.0

    def puct_score(self, sqrt_parent_n: float) -> float:
        n_eff = self.visit_count + self.virtual_loss
        return -self.q_value + C_PUCT * self.prior * sqrt_parent_n / (1 + n_eff)


class BatchedNativeMCTS:
    """
    NativeGameEnv（shogi_expand バッチ API）を使った高速 Gumbel MCTS。
    """

    def __init__(self, env, net, device, n_sims: int = 400, batch_size: int = 8):
        self.env        = env   # NativeGameEnv のインスタンス
        self.net        = net
        self.device     = device
        self.n_sims     = n_sims
        self.batch_size = batch_size

    def run(self, sfen: str, temperature: float = 1.0,
            add_noise: bool = True) -> np.ndarray:
        from game_env import GameEnv
        root = self._setup_root(sfen)
        if root.is_terminal or not root.children:
            return np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32), 0.0

        actions   = list(root.children.keys())
        log_prior = np.log([root.children[a].prior + 1e-8 for a in actions])
        gumbel    = np.random.gumbel(0, 1, len(actions))
        gumbel_scores = log_prior + gumbel

        sims_done = 1
        while sims_done < self.n_sims:
            batch_n = min(self.batch_size, self.n_sims - sims_done)

            # Phase A: batch_n 本の経路を選択
            paths = [self._select(root, actions, gumbel_scores)
                     for _ in range(batch_n)]
            leaves = [p[-1] for p in paths]

            # Phase B: 新しいリーフを expand API で一括評価
            to_eval_idx = [i for i, l in enumerate(leaves)
                           if not l.is_terminal and not l.is_expanded]

            leaf_values = [None] * batch_n
            for i, l in enumerate(leaves):
                if l.is_terminal:
                    leaf_values[i] = l.terminal_value

            if to_eval_idx:
                # shogi_expand で sfen + tensor + moves を 1 呼び出しで取得
                tensors_list = []
                expand_results = {}

                for i in to_eval_idx:
                    leaf = leaves[i]
                    # バッチ API: apply + tensor + legal_moves を 1 呼び出し
                    new_sfen, tensor, moves = self.env.expand(
                        leaf.parent.sfen, leaf.move
                    )
                    leaf.sfen = new_sfen   # 遅延 SFEN を確定

                    if moves is None:       # terminal
                        leaf.is_terminal    = True
                        leaf.terminal_value = -1.0
                        leaf_values[i]      = -1.0
                    else:
                        expand_results[i] = (tensor, moves)
                        tensors_list.append(tensor)

                # NN バッチ推論
                if tensors_list:
                    tensors_arr = np.stack(tensors_list)
                    policies, values = self.net.predict_batch(
                        tensors_arr, self.device
                    )
                    eval_idx_in_batch = [i for i in to_eval_idx
                                        if i in expand_results]
                    for k, i in enumerate(eval_idx_in_batch):
                        _, moves = expand_results[i]
                        leaf = leaves[i]
                        for m in moves:
                            leaf.children[m] = Node(
                                sfen=None, parent=leaf, move=m,
                                prior=float(policies[k][m])
                            )
                        leaf.is_expanded = True
                        leaf_values[i]   = float(values[k])

            # already-expanded leaf
            for i, v in enumerate(leaf_values):
                if v is None:
                    leaf_values[i] = 0.0

            # Phase C: バックアップ
            for path, value in zip(paths, leaf_values):
                self._backup(path, value)

            sims_done += batch_n

        # 改善ポリシーを計算
        c      = max(1.0, math.sqrt(max(root.visit_count, 1)))
        cq_all = {a: -root.children[a].q_value
                  if root.children[a].visit_count > 0 else 0.0
                  for a in actions}
        logits = {a: log_prior[i] + c * _sigmoid(cq_all[a])
                  for i, a in enumerate(actions)}
        policy     = self._to_policy(logits, actions, temperature)
        root_value = root.q_value   # 現在手番プレイヤー視点の価値推定
        return policy, root_value

    def _setup_root(self, sfen: str) -> Node:
        root = Node(sfen)
        # ルートは to_tensor + legal_moves の 2 呼び出し（apply は不要）
        if self.env.is_terminal(sfen):
            root.is_terminal    = True
            root.terminal_value = -1.0
            root.is_expanded    = True
            return root

        tensor   = self.env.to_tensor(sfen)
        policies, values = self.net.predict_batch(
            np.expand_dims(tensor, 0), self.device
        )
        policy, value = policies[0], float(values[0])

        for m in self.env.legal_moves(sfen):
            root.children[m] = Node(sfen=None, parent=root, move=m,
                                    prior=float(policy[m]))
        root.is_expanded = True
        root.visit_count = 1
        root.value_sum   = value
        return root

    def _select(self, root: Node, actions: list, gumbel_scores: np.ndarray):
        path = [root]
        root.virtual_loss += 1

        c = max(1.0, math.sqrt(max(root.visit_count, 1)))
        scores = np.array([
            gumbel_scores[i] + c * _sigmoid(-root.children[a].q_value)
            for i, a in enumerate(actions)
        ])
        forced = actions[int(np.argmax(scores))]

        child = root.children[forced]
        # SFEN が未確定のまま selection を進める（expand 時に確定）
        if child.sfen is None:
            child.sfen = self.env.apply(root.sfen, forced)
        child.virtual_loss += 1
        path.append(child)
        node = child

        while node.is_expanded and not node.is_terminal and node.children:
            sqrt_n = math.sqrt(max(node.visit_count + node.virtual_loss, 1))
            node   = max(node.children.values(),
                         key=lambda c: c.puct_score(sqrt_n))
            if node.sfen is None:
                node.sfen = self.env.apply(node.parent.sfen, node.move)
            node.virtual_loss += 1
            path.append(node)

        return path

    def _backup(self, path: list, value: float):
        for node in reversed(path):
            node.virtual_loss -= 1
            node.visit_count  += 1
            node.value_sum    += value
            value = -value

    def _to_policy(self, logits, actions, temperature):
        from game_env import GameEnv
        policy = np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32)
        if temperature < 0.01:
            policy[max(actions, key=lambda a: logits[a])] = 1.0
        else:
            vals  = np.array([logits[a] for a in actions]) / temperature
            vals -= vals.max()
            probs = np.exp(vals) / np.exp(vals).sum()
            for a, p in zip(actions, probs):
                policy[a] = float(p)
        return policy


def _sigmoid(x: float) -> float:
    x = max(-20.0, min(20.0, x))
    return 1.0 / (1.0 + math.exp(-x))
