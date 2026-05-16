"""
バッチ推論対応 Gumbel MCTS

通常の MCTS との違い:
  通常: 1 sim → 1 NN 推論（GPU をほぼ使わない）
  バッチ: batch_size sims の末尾ノードを溜める
         → まとめて 1 回の GPU フォワードパス
         → GPU 使用率が大幅に向上

Virtual loss:
  複数シミュレーションが同じ経路を選ばないよう、
  選択中のノードに仮の負の価値を付与する。
  バックアップ時に実際の価値に置き換える。
"""

import math
import numpy as np
from typing import Optional

C_PUCT = 1.25


# ── ノード ────────────────────────────────────────────────────────
class Node:
    __slots__ = ('sfen', 'parent', 'move', 'prior',
                 'children', 'visit_count', 'value_sum',
                 'is_expanded', 'is_terminal', 'terminal_value',
                 'virtual_loss')

    def __init__(self, sfen: Optional[str] = None,
                 parent: Optional['Node'] = None,
                 move: Optional[int] = None,
                 prior: float = 0.0):
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
        self.virtual_loss   = 0   # バッチ処理中の仮の訪問数

    @property
    def q_value(self) -> float:
        n = self.visit_count + self.virtual_loss
        if n == 0:
            return 0.0
        # virtual_loss 分は -1 の価値として計算（探索忌避）
        return (self.value_sum - self.virtual_loss) / n

    def puct_score(self, sqrt_parent_n: float) -> float:
        n_eff = self.visit_count + self.virtual_loss
        q = -self.q_value
        u = C_PUCT * self.prior * sqrt_parent_n / (1 + n_eff)
        return q + u


# ── Batched Gumbel MCTS ───────────────────────────────────────────
class BatchedGumbelMCTS:
    """
    使い方:
        mcts = BatchedGumbelMCTS(env, net, device,
                                 n_sims=400, batch_size=8)
        policy = mcts.run(sfen, temperature=1.0)

    batch_size の目安:
        T4  GPU: 8〜16（NN推論がボトルネック → バッチで解消）
        CPU    : 1〜4 （効果は限定的）
    """

    def __init__(self, env, net, device,
                 n_sims: int = 400, batch_size: int = 8):
        self.env        = env
        self.net        = net
        self.device     = device
        self.n_sims     = n_sims
        self.batch_size = batch_size

    # ── 公開 API ─────────────────────────────────────────────────

    def run(self, sfen: str, temperature: float = 1.0,
            add_noise: bool = True) -> np.ndarray:
        # add_noise は Gumbel MCTS では常に有効（Gumbel ノイズが代替）
        from game_env import GameEnv

        # ① ルートを展開（1回の NN 推論）
        root = self._setup_root(sfen)
        if root.is_terminal or not root.children:
            return np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32), 0.0

        actions    = list(root.children.keys())
        log_prior  = np.log([root.children[a].prior + 1e-8 for a in actions])
        gumbel     = np.random.gumbel(0, 1, len(actions))
        gumbel_scores = log_prior + gumbel   # Gumbel ノイズ（ルート専用、固定）

        # ② バッチ単位でシミュレーション
        sims_done = 1   # ルート展開を 1 sims 分とカウント
        while sims_done < self.n_sims:
            batch_n = min(self.batch_size, self.n_sims - sims_done)

            # Phase A: batch_n 本の経路を選択（virtual loss 付加）
            paths = [self._select(root, actions, gumbel_scores)
                     for _ in range(batch_n)]
            leaves = [p[-1] for p in paths]

            # Phase B: 新しいリーフを一括で NN 評価（バッチ推論）
            to_eval_idx = [i for i, l in enumerate(leaves)
                           if not l.is_terminal and not l.is_expanded]

            leaf_values = [None] * batch_n
            for i, l in enumerate(leaves):
                if l.is_terminal:
                    leaf_values[i] = l.terminal_value

            if to_eval_idx:
                tensors = np.stack([
                    self.env.to_tensor(leaves[i].sfen) for i in to_eval_idx
                ])
                policies, values = self.net.predict_batch(tensors, self.device)

                for k, i in enumerate(to_eval_idx):
                    self._expand(leaves[i], policies[k])
                    leaf_values[i] = float(values[k])

            # already-expanded leaf (稀) は価値 0 として扱う
            for i, v in enumerate(leaf_values):
                if v is None:
                    leaf_values[i] = 0.0

            # Phase C: 全経路をバックアップ（virtual loss 解除 + 価値反映）
            for path, value in zip(paths, leaf_values):
                self._backup(path, value)

            sims_done += batch_n

        # ③ 改善ポリシーを計算（Gumbel ノイズなし → 学習ターゲット）
        c       = max(1.0, math.sqrt(max(root.visit_count, 1)))
        cq_all  = {a: -root.children[a].q_value
                   if root.children[a].visit_count > 0 else 0.0
                   for a in actions}
        logits  = {a: log_prior[i] + c * _sigmoid(cq_all[a])
                   for i, a in enumerate(actions)}

        policy     = self._to_policy(logits, actions, temperature)
        root_value = root.q_value
        return policy, root_value

    # ── 内部メソッド ──────────────────────────────────────────────

    def _setup_root(self, sfen: str) -> Node:
        """ルートを展開し最初の価値をセット。"""
        root = Node(sfen)
        if self.env.is_terminal(sfen):
            root.is_terminal    = True
            root.terminal_value = -1.0
            root.is_expanded    = True
            return root

        tensor = self.env.to_tensor(sfen)
        policies, values = self.net.predict_batch(
            np.expand_dims(tensor, 0), self.device
        )
        policy, value = policies[0], float(values[0])

        for m in self.env.legal_moves(sfen):
            root.children[m] = Node(sfen=None, parent=root, move=m,
                                    prior=float(policy[m]))
        root.is_expanded  = True
        root.visit_count  = 1
        root.value_sum    = value
        return root

    def _select(self, root: Node, actions: list,
                gumbel_scores: np.ndarray) -> list:
        """
        1本の経路を選択し virtual loss を付加する。
        ルート: Gumbel + completed Q でガイド。
        非ルート: PUCT。
        """
        path = [root]
        root.virtual_loss += 1   # ルートも virtual loss 対象

        # ルート直下の子を Gumbel スコアで選択
        c = max(1.0, math.sqrt(max(root.visit_count, 1)))
        scores = np.array([
            gumbel_scores[i] + c * _sigmoid(-root.children[a].q_value)
            for i, a in enumerate(actions)
        ])
        forced = actions[int(np.argmax(scores))]

        child = root.children[forced]
        child.sfen = child.sfen or self.env.apply(root.sfen, forced)
        child.virtual_loss += 1
        path.append(child)
        node = child

        # 以降は PUCT
        while node.is_expanded and not node.is_terminal and node.children:
            sqrt_n = math.sqrt(max(node.visit_count + node.virtual_loss, 1))
            node   = max(node.children.values(),
                         key=lambda c: c.puct_score(sqrt_n))
            node.sfen = node.sfen or self.env.apply(node.parent.sfen, node.move)
            node.virtual_loss += 1
            path.append(node)

        return path

    def _expand(self, node: Node, policy: np.ndarray):
        """リーフノードを展開（NN 評価後に呼ぶ）。"""
        node.is_expanded = True
        for m in self.env.legal_moves(node.sfen):
            node.children[m] = Node(sfen=None, parent=node, move=m,
                                    prior=float(policy[m]))

    def _backup(self, path: list, value: float):
        """
        経路を遡りながら価値を伝播。
        virtual loss を解除し実際の訪問数・価値を更新。
        """
        for node in reversed(path):
            node.virtual_loss -= 1      # virtual loss 解除
            node.visit_count  += 1
            node.value_sum    += value
            value = -value

    def _to_policy(self, logits: dict, actions: list,
                   temperature: float) -> np.ndarray:
        from game_env import GameEnv
        policy = np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32)
        if temperature < 0.01:
            policy[max(actions, key=lambda a: logits[a])] = 1.0
        else:
            vals  = np.array([logits[a] for a in actions]) / temperature
            vals -= vals.max()
            probs = np.exp(vals)
            probs /= probs.sum()
            for a, p in zip(actions, probs):
                policy[a] = float(p)
        return policy


def _sigmoid(x: float) -> float:
    x = max(-20.0, min(20.0, x))
    return 1.0 / (1.0 + math.exp(-x))
