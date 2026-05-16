"""
Gumbel AlphaZero MCTS
"Policy improvement by planning with Gumbel" (Amos et al., 2022)

実装の方針:
  n_sims ≥ 合法手数 → Sequential Halving（論文の本来の形）
  n_sims <  合法手数 → Gumbel guided MCTS（小 budget 向け簡易版）

どちらの場合も学習ターゲットは「改善ポリシー」（訪問数ではなく
completed Q-value から算出）を使う。
"""

import math
import numpy as np
from typing import Optional

C_PUCT = 1.25   # 非ルートノードの PUCT 定数


# ── ノード ────────────────────────────────────────────────────────
class Node:
    __slots__ = ('sfen', 'parent', 'move', 'prior',
                 'children', 'visit_count', 'value_sum',
                 'is_expanded', 'is_terminal', 'terminal_value')

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

    @property
    def q_value(self) -> float:
        return self.value_sum / self.visit_count if self.visit_count > 0 else 0.0

    def puct_score(self, sqrt_parent_n: float) -> float:
        q = -self.q_value
        u = C_PUCT * self.prior * sqrt_parent_n / (1 + self.visit_count)
        return q + u


# ── Gumbel MCTS ───────────────────────────────────────────────────
class GumbelMCTS:
    """
    パラメータ目安:
      CPU  : n_sims = 16〜32
      T4   : n_sims = 64〜128
      A100 : n_sims = 256〜512
    """

    def __init__(self, env, net, device, n_sims: int = 32):
        self.env    = env
        self.net    = net
        self.device = device
        self.n_sims = n_sims

    def run(self, sfen: str, temperature: float = 1.0) -> np.ndarray:
        from game_env import GameEnv
        root = Node(sfen)
        self._expand(root)

        if root.is_terminal or not root.children:
            return np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32), 0.0

        actions   = list(root.children.keys())
        m         = len(actions)
        log_prior = np.log([root.children[a].prior + 1e-8 for a in actions])
        gumbel    = np.random.gumbel(0, 1, m)           # ルートのみ使用

        # Gumbel guided MCTS
        # Gumbel + completed Q-value でルートの手選択を誘導する。
        # Sequential Halving は n_sims >> m のとき有効だが、
        # 典型的な局面(m≈60)で有効になるのは n_sims ≥ 360 程度のため、
        # ここでは常にシンプルな Gumbel guided を使う。
        init_scores = log_prior + gumbel
        action_arr  = np.array(actions)

        for _ in range(self.n_sims):
            # 現在の completed Q を反映してスコアを再計算
            c = max(1.0, math.sqrt(max(root.visit_count, 1)))
            scores = np.array([
                init_scores[i] + c * _sigmoid(
                    -root.children[a].q_value if root.children[a].visit_count > 0 else 0.0
                )
                for i, a in enumerate(actions)
            ])
            forced = action_arr[int(np.argmax(scores))]
            self._simulate(root, forced_first=int(forced))

        # ── 改善ポリシーを計算（Gumbel ノイズなし → 学習ターゲット）──
        c       = max(1.0, math.sqrt(max(root.visit_count, 1)))
        cq_all  = self._completed_q(root, actions)
        logits  = {a: log_prior[i] + c * _sigmoid(cq_all[a])
                   for i, a in enumerate(actions)}

        policy     = self._to_policy(logits, actions, temperature)
        root_value = root.q_value
        return policy, root_value

    # ── Sequential Halving ────────────────────────────────────────

    def _sequential_halving(self, root: 'Node', actions: list,
                            init_scores: np.ndarray):
        """
        Budget: n_sims 内で Sequential Halving を実行。
        各フェーズの予算 = floor(n_sims / n_phases)
        """
        m        = len(actions)
        n_phases = max(1, math.ceil(math.log2(max(m, 2))))
        budget_per_phase = max(1, self.n_sims // n_phases)

        candidates = list(zip(actions, init_scores))

        for _ in range(n_phases):
            if len(candidates) <= 1:
                break
            n_cand       = len(candidates)
            sims_per_cand = max(1, budget_per_phase // n_cand)

            for a, _ in candidates:
                for _ in range(sims_per_cand):
                    self._simulate(root, forced_first=a)

            # Completed Q-value + Gumbel スコアで再評価
            c  = max(1.0, math.sqrt(max(root.visit_count, 1)))
            cq = self._completed_q(root, [a for a, _ in candidates])
            rescored = sorted(
                [(a, g + c * _sigmoid(cq[a])) for a, g in candidates],
                key=lambda x: x[1], reverse=True
            )
            candidates = rescored[: max(1, n_cand // 2)]

    # ── シミュレーション ──────────────────────────────────────────

    def _simulate(self, root: 'Node', forced_first: Optional[int] = None):
        path = [root]
        node = root

        if forced_first is not None and forced_first in root.children:
            child = root.children[forced_first]
            child.sfen = child.sfen or self.env.apply(root.sfen, forced_first)
            path.append(child)
            node = child

        while node.is_expanded and not node.is_terminal and node.children:
            sqrt_n = math.sqrt(max(node.visit_count, 1))
            node   = max(node.children.values(), key=lambda c: c.puct_score(sqrt_n))
            node.sfen = node.sfen or self.env.apply(node.parent.sfen, node.move)
            path.append(node)

        if node.is_terminal:
            value = node.terminal_value
        elif not node.is_expanded:
            value = self._expand(node)
        else:
            value = 0.0

        for n in reversed(path):
            n.visit_count += 1
            n.value_sum   += value
            value = -value

    def _expand(self, node: 'Node') -> float:
        node.is_expanded = True
        if self.env.is_terminal(node.sfen):
            node.is_terminal    = True
            node.terminal_value = -1.0
            return -1.0
        tensor = self.env.to_tensor(node.sfen)
        policy, value = self.net.predict(tensor, self.device)
        for m in self.env.legal_moves(node.sfen):
            node.children[m] = Node(sfen=None, parent=node, move=m,
                                    prior=float(policy[m]))
        return value

    # ── Completed Q-value ─────────────────────────────────────────

    def _completed_q(self, root: 'Node', actions: list) -> dict:
        """
        親視点の Completed Q-value。
        未訪問ノードには訪問済みの加重平均 v_mix を補完。
        """
        visited = {a: root.children[a] for a in actions
                   if root.children[a].visit_count > 0}
        if visited:
            total = sum(c.visit_count for c in visited.values())
            v_mix = sum(-c.q_value * c.visit_count for c in visited.values()) / total
        else:
            v_mix = 0.0
        return {a: (-root.children[a].q_value
                    if root.children[a].visit_count > 0 else v_mix)
                for a in actions}

    # ── ポリシー変換 ──────────────────────────────────────────────

    def _to_policy(self, logits: dict, actions: list,
                   temperature: float) -> np.ndarray:
        from game_env import GameEnv
        policy = np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32)
        if temperature < 0.01:
            policy[max(actions, key=lambda a: logits[a])] = 1.0
        else:
            vals = np.array([logits[a] for a in actions]) / temperature
            vals -= vals.max()
            probs = np.exp(vals)
            probs /= probs.sum()
            for a, p in zip(actions, probs):
                policy[a] = float(p)
        return policy


def _sigmoid(x: float) -> float:
    x = max(-20.0, min(20.0, x))
    return 1.0 / (1.0 + math.exp(-x))
