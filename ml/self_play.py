"""
自己対局によるトレーニングデータ生成とリプレイバッファ管理。

改良点:
  - KataGo 補助タスク: ownership ラベルを生成
  - Value Prefix: N 手先の MCTS 価値でブートストラップした学習ターゲット
  - 優先度付き経験再生 (PER): TD 誤差に基づいてサンプリング優先度を管理
"""

import numpy as np
import random
from collections import deque
from typing import NamedTuple
from mcts import TEMP_THRESHOLD, MAX_GAME_LEN

# 価値プレフィックスのホライズン（N 手先でブートストラップ）
VALUE_PREFIX_N = 5


# ── データサンプル ────────────────────────────────────────────────
class Sample(NamedTuple):
    tensor        : np.ndarray   # (47, 9, 9) float32
    policy        : np.ndarray   # (ACTION_SIZE,) float32
    value         : float        # 終局結果 (-1〜+1)
    value_prefix  : float        # N 手先ブートストラップ価値 (-1〜+1)
    ownership     : np.ndarray   # (81,) float32  KataGo 補助タスク


# ── 優先度付きリプレイバッファ (PER) ─────────────────────────────
class PrioritizedReplayBuffer:
    """
    TD 誤差に基づいて優先度を管理するリプレイバッファ。
    priority^alpha の確率でサンプリング。
    バイアス補正には重要度サンプリング重みを使用。
    """

    def __init__(self, max_size: int = 200_000,
                 alpha: float = 0.6, beta: float = 0.4):
        self.max_size  = max_size
        self.alpha     = alpha    # 優先度の鋭さ（0=均等, 1=完全優先度）
        self.beta      = beta     # バイアス補正の強さ（0=なし, 1=完全補正）
        self._buf: list[Sample]     = []
        self._priorities: np.ndarray = np.zeros(max_size, dtype=np.float32)
        self._pos          = 0
        self._max_priority = 1.0

    def add_game(self, samples: list[Sample]):
        for s in samples:
            if len(self._buf) < self.max_size:
                self._buf.append(s)
            else:
                self._buf[self._pos] = s
            self._priorities[self._pos] = self._max_priority
            self._pos = (self._pos + 1) % self.max_size

    def sample(self, batch_size: int) -> tuple:
        """
        (samples, weights, indices) を返す。
        weights は importance sampling 重み（損失に掛けてバイアスを補正）。
        """
        n = len(self._buf)
        probs = self._priorities[:n] ** self.alpha
        probs /= probs.sum()

        indices = np.random.choice(n, min(batch_size, n), p=probs, replace=False)
        samples = [self._buf[i] for i in indices]

        # Importance sampling weights（大きい priority ほど重みが小さくなる）
        weights = (n * probs[indices]) ** (-self.beta)
        weights /= weights.max()

        tensors   = np.stack([s.tensor   for s in samples])
        policies  = np.stack([s.policy   for s in samples])
        values    = np.array([s.value    for s in samples], dtype=np.float32)
        vprefixes = np.array([s.value_prefix for s in samples], dtype=np.float32)
        ownerships= np.stack([s.ownership for s in samples])

        return tensors, policies, values, vprefixes, ownerships, \
               weights.astype(np.float32), indices

    def update_priorities(self, indices: np.ndarray, errors: np.ndarray):
        """学習後に TD 誤差から優先度を更新する。"""
        for idx, err in zip(indices, errors):
            p = float(abs(err)) + 1e-6
            self._priorities[idx] = p
        self._max_priority = float(self._priorities[:len(self._buf)].max())

    def __len__(self) -> int:
        return len(self._buf)


# ── 1 局の自己対局 ────────────────────────────────────────────────
def generate_game(env, mcts, add_noise: bool = True) -> list[Sample]:
    """
    MCTS 自己対局を 1 局行い、学習サンプルのリストを返す。

    各サンプルは:
      - value:        最終結果（手番プレイヤー視点）
      - value_prefix: N 手先の MCTS 価値でブートストラップした価値
      - ownership:    現局面の各升の帰属（利きビットボードから計算）
    """
    sfen    = env.initial_sfen()
    history = []   # (tensor, policy, player, mcts_value)
    move_count = 0

    while True:
        if env.is_terminal(sfen) or move_count >= MAX_GAME_LEN:
            break

        temp   = 1.0 if move_count < TEMP_THRESHOLD else 1e-4
        policy, mcts_value = mcts.run(sfen, temperature=temp, add_noise=add_noise)
        tensor    = env.to_tensor(sfen)
        ownership = env.ownership(sfen)   # KataGo 補助タスク
        player    = env.current_player(sfen)

        history.append((tensor, policy, player, mcts_value, ownership))

        legal = env.legal_moves(sfen)
        p_legal = np.array([policy[m] for m in legal], dtype=np.float32)
        s = p_legal.sum()
        p_legal = p_legal / s if s > 0 else np.ones(len(legal)) / len(legal)

        sfen = env.apply(sfen, int(legal[np.random.choice(len(legal), p=p_legal)]))
        move_count += 1

    result = env.result(sfen)
    if result == 0:
        result = -1  # 超手数 → 後手勝ち
    T      = len(history)
    samples = []

    for t, (tensor, policy, player, mcts_val, ownership) in enumerate(history):
        # 終局結果を手番視点に変換
        if result == 0:
            v = 0.0
        elif player == "先手":
            v = float(result)
        else:
            v = float(-result)

        # Value Prefix: N 手先の MCTS 価値でブートストラップ
        n = VALUE_PREFIX_N
        if t + n < T:
            future_player = history[t + n][2]
            future_mcts   = history[t + n][3]
            # 同じ手番なら符号そのまま、違う手番なら反転
            vp = future_mcts if player == future_player else -future_mcts
        else:
            vp = v  # 残り手数が N 未満なら終局結果を使う

        # Ownership は終局結果で符号を調整（勝者の利きを正にする）
        own_signed = ownership * (1.0 if v >= 0 else -1.0)

        samples.append(Sample(tensor, policy, v, vp, own_signed.astype(np.float32)))

    return samples


# ── 複数局を並べて生成 ────────────────────────────────────────────
def generate_games(env, mcts, num_games: int,
                   buf: PrioritizedReplayBuffer, verbose: bool = True):
    from tqdm import tqdm
    for _ in tqdm(range(num_games), desc="self-play", disable=not verbose):
        buf.add_game(generate_game(env, mcts))
    if verbose:
        print(f"  buffer size: {len(buf):,}")
