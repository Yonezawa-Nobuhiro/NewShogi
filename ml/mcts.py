"""
MCTS (AlphaZero 方式)
PUCT 式: Q(s,a) + C_puct * P(s,a) * sqrt(N(s)) / (1 + N(s,a))
  Q(s,a) は親ノードの手番プレイヤー視点 = -child.q_value
"""

import math
import numpy as np
from typing import Optional

# ── ハイパーパラメータ ─────────────────────────────────────────────
C_PUCT           = 1.25
DIR_ALPHA        = 0.3    # ディリクレノイズの集中度（将棋は小さめ）
DIR_EPSILON      = 0.25   # ルートノードのノイズ混合率
MAX_GAME_LEN     = 300    # これ以上は後手勝ち扱い
TEMP_THRESHOLD   = 30     # この手数以降は温度=0（最善手のみ）


# ── ノード ────────────────────────────────────────────────────────────
class Node:
    __slots__ = (
        "sfen", "parent", "move", "prior",
        "children", "visit_count", "value_sum",
        "is_expanded", "is_terminal", "terminal_value",
    )

    def __init__(self, sfen: Optional[str], parent: Optional["Node"] = None,
                 move: Optional[int] = None, prior: float = 0.0):
        self.sfen           = sfen
        self.parent         = parent
        self.move           = move    # 親からこのノードへの手インデックス
        self.prior          = prior
        self.children: dict[int, "Node"] = {}
        self.visit_count    = 0
        self.value_sum      = 0.0
        self.is_expanded    = False
        self.is_terminal    = False
        self.terminal_value = 0.0

    @property
    def q_value(self) -> float:
        """このノードの手番プレイヤー視点の期待値。"""
        return self.value_sum / self.visit_count if self.visit_count > 0 else 0.0

    def puct_score(self, sqrt_parent_n: float) -> float:
        # 親視点の Q = -child.q_value（子の手番は親と逆なので符号反転）
        q = -self.q_value
        u = C_PUCT * self.prior * sqrt_parent_n / (1 + self.visit_count)
        return q + u


# ── MCTS ─────────────────────────────────────────────────────────────
class MCTS:
    def __init__(self, env, net, device, num_simulations: int = 200):
        self.env             = env
        self.net             = net
        self.device          = device
        self.num_simulations = num_simulations

    # ── 公開 API ─────────────────────────────────────────────────────

    def run(self, sfen: str, temperature: float = 1.0,
            add_noise: bool = False) -> np.ndarray:
        """
        MCTS を実行し、手のポリシー分布（shape: ACTION_SIZE）を返す。
        temperature=0 相当（< 1e-4）で最善手に確率 1。
        """
        root = Node(sfen)

        for sim_idx in range(self.num_simulations):
            # ① 選択
            node, path = self._select(root)

            # ② 展開・評価
            if node.is_terminal:
                value = node.terminal_value
            elif not node.is_expanded:
                value = self._expand(node)
                # ルートの初回展開後にディリクレノイズを追加
                if add_noise and node is root and root.children:
                    self._add_dirichlet_noise(root)
            else:
                value = 0.0  # already expanded, no children (shouldn't happen)

            # ③ バックアップ
            self._backup(path, value)

        policy     = self._get_policy(root, temperature)
        root_value = root.q_value
        return policy, root_value

    # ── 内部メソッド ──────────────────────────────────────────────────

    def _select(self, root: Node) -> tuple["Node", list["Node"]]:
        """PUCT に従いリーフまで降りる。SFEN は遅延計算。"""
        node = root
        path = [node]
        while node.is_expanded and not node.is_terminal and node.children:
            sqrt_n = math.sqrt(node.visit_count) if node.visit_count > 0 else 0.0
            node = max(node.children.values(),
                       key=lambda c: c.puct_score(sqrt_n))
            # 遅延 SFEN 計算
            if node.sfen is None:
                node.sfen = self.env.apply(node.parent.sfen, node.move)
            path.append(node)
        return node, path

    def _expand(self, node: Node) -> float:
        """
        ノードを展開してニューラルネットで評価する。
        戻り値: このノードの手番プレイヤー視点の価値推定。
        """
        node.is_expanded = True

        if self.env.is_terminal(node.sfen):
            node.is_terminal    = True
            node.terminal_value = -1.0  # 手番側（合法手なし）= 負け
            return -1.0

        tensor = self.env.to_tensor(node.sfen)
        policy, value = self.net.predict(tensor, self.device)

        for m in self.env.legal_moves(node.sfen):
            node.children[m] = Node(
                sfen=None, parent=node, move=m, prior=float(policy[m])
            )
        return value

    def _backup(self, path: list["Node"], value: float):
        """
        リーフから根へ遡りながら訪問数と価値を更新する。
        符号は 1 手ごとに反転（手番が交互に変わるため）。
        """
        for node in reversed(path):
            node.visit_count += 1
            node.value_sum   += value
            value = -value

    def _add_dirichlet_noise(self, root: Node):
        moves = list(root.children.keys())
        noise = np.random.dirichlet([DIR_ALPHA] * len(moves))
        for m, n in zip(moves, noise):
            c = root.children[m]
            c.prior = (1 - DIR_EPSILON) * c.prior + DIR_EPSILON * float(n)

    def _get_policy(self, root: Node, temperature: float) -> np.ndarray:
        """訪問数から手のポリシー分布を作る。"""
        from game_env import GameEnv
        policy = np.zeros(GameEnv.ACTION_SIZE, dtype=np.float32)
        counts = {m: c.visit_count for m, c in root.children.items()}
        if not counts:
            return policy

        if temperature < 0.01:
            best = max(counts, key=counts.get)
            policy[best] = 1.0
        else:
            inv_t = 1.0 / temperature
            weighted = {m: cnt ** inv_t for m, cnt in counts.items()}
            total = sum(weighted.values())
            for m, w in weighted.items():
                policy[m] = w / total
        return policy
