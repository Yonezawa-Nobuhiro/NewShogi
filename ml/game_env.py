"""
変成将棋ゲーム環境 (Python ラッパー)
C# の ShogiBridge.Api クラスを pythonnet 経由で呼び出す。

使い方:
    from game_env import GameEnv
    env = GameEnv()
    sfen = env.initial_sfen()
    moves = env.legal_moves(sfen)
    sfen = env.apply(sfen, moves[0])
    tensor = env.to_tensor(sfen)   # shape (47, 9, 9)
"""

import sys
import os
import numpy as np

# ── C# エンジン DLL のロード ────────────────────────────────────────────
# 環境変数 SHOGI_ENGINE_DLL_DIR があればそちらを優先（Colab 用）
_ENGINE_DLL_DIR = os.environ.get(
    "SHOGI_ENGINE_DLL_DIR",
    os.path.abspath(os.path.join(os.path.dirname(__file__),
                                 "..", "変成将棋.Engine", "bin", "Release", "net8.0"))
)

def _load_engine():
    import pythonnet
    # dotnet_root を明示（Colab など PATH が通っていない環境用）
    dotnet_root = os.environ.get("DOTNET_ROOT", None)
    if dotnet_root:
        pythonnet.load("coreclr", dotnet_root=dotnet_root)
    else:
        pythonnet.load("coreclr")
    import clr

    if _ENGINE_DLL_DIR not in sys.path:
        sys.path.insert(0, _ENGINE_DLL_DIR)

    clr.AddReference("変成将棋.Engine")
    from ShogiBridge import Api  # type: ignore  (ASCII namespace)
    return Api

_api = None

def _get_api():
    global _api
    if _api is None:
        _api = _load_engine()
    return _api


# ── GameEnv ────────────────────────────────────────────────────────────
class GameEnv:
    """変成将棋の局面操作を提供するシンプルなラッパー。"""

    ACTION_SIZE: int = 18_873
    TENSOR_CHANNELS: int = 47
    BOARD_SIZE: int = 9

    def initial_sfen(self) -> str:
        return str(_get_api().initial_sfen())

    def legal_moves(self, sfen: str) -> list[int]:
        return list(_get_api().legal_moves(sfen))

    def apply(self, sfen: str, move_index: int) -> str:
        return str(_get_api().apply_move(sfen, move_index))

    def is_terminal(self, sfen: str) -> bool:
        return bool(_get_api().is_terminal(sfen))

    def result(self, sfen: str) -> int:
        """1=先手勝ち, -1=後手勝ち, 0=進行中。"""
        return int(_get_api().result(sfen))

    def current_player(self, sfen: str) -> str:
        return str(_get_api().current_player(sfen))

    def to_tensor(self, sfen: str) -> np.ndarray:
        """shape: (47, 9, 9), dtype=float32"""
        flat = list(_get_api().board_tensor(sfen))
        return np.array(flat, dtype=np.float32).reshape(
            self.TENSOR_CHANNELS, self.BOARD_SIZE, self.BOARD_SIZE
        )

    def play_random_game(self, max_moves: int = 500) -> list[str]:
        """ランダム自己対局1局。SFEN 履歴を返す。"""
        import random
        sfen = self.initial_sfen()
        history = [sfen]
        for _ in range(max_moves):
            if self.is_terminal(sfen):
                break
            sfen = self.apply(sfen, random.choice(self.legal_moves(sfen)))
            history.append(sfen)
        return history
