"""
NativeAOT + ctypes を使ったゲーム環境。
Python.NET ブリッジ (~8ms/call) の代わりに ctypes (~0.1ms/call) で C# DLL を直接呼ぶ。

使い方:
    from native_game_env import NativeGameEnv
    env = NativeGameEnv('/path/to/shogi_engine.so')   # Linux
    env = NativeGameEnv('/path/to/shogi_engine.dll')  # Windows

GameEnv と同じインターフェースを提供するので batched_mcts.py をそのまま使える。
"""

import ctypes
import numpy as np
import os
import sys
from pathlib import Path


# ── DLL の場所を自動検索 ──────────────────────────────────────────
def _find_native_lib() -> str:
    """環境に応じた shogi_engine DLL のパスを探す。"""

    # 環境変数が設定されていれば最優先（Colab の Cell 4 がここをセットする）
    env_path = os.environ.get("SHOGI_NATIVE_LIB", "")
    if env_path and Path(env_path).exists():
        return env_path

    publish_base = Path(__file__).parent.parent / "変成将棋.Engine.Native" / "bin" / "Release" / "net8.0"

    if sys.platform == "win32":
        candidates = [publish_base / "win-x64" / "publish" / "shogi_engine.dll"]
    else:
        candidates = [
            publish_base / "linux-x64" / "publish" / "shogi_engine.so",
            publish_base / "linux-x64" / "publish" / "libshogi_engine.so",
        ]

    for p in candidates:
        if p.exists():
            return str(p)

    raise FileNotFoundError(
        "shogi_engine の NativeAOT DLL が見つかりません。\n"
        "dotnet publish でビルドしてから使用してください。\n"
        "環境変数 SHOGI_NATIVE_LIB でパスを指定することもできます。"
    )


# ── NativeGameEnv ─────────────────────────────────────────────────
class NativeGameEnv:
    """
    ctypes 経由で C# NativeAOT DLL を呼ぶゲーム環境。
    GameEnv と同じ API を提供する。
    """

    ACTION_SIZE    : int = 18_873
    TENSOR_CHANNELS: int = 47
    BOARD_SIZE     : int = 9
    _TENSOR_LEN    : int = 47 * 81
    _MAX_MOVES     : int = 600
    _SFEN_BUF      : int = 256

    def __init__(self, lib_path: str | None = None):
        path = lib_path or _find_native_lib()
        self._lib = ctypes.CDLL(path)
        self._setup()
        # ── 再利用バッファ（毎呼び出しで確保しない）──────────────
        self._sfen_buf      = ctypes.create_string_buffer(self._SFEN_BUF)
        self._tensor_buf    = (ctypes.c_float * self._TENSOR_LEN)()
        self._moves_buf     = (ctypes.c_int   * self._MAX_MOVES)()
        self._ownership_buf = (ctypes.c_float * 81)()

    def _setup(self):
        """ctypes 関数シグネチャを登録。"""
        lib = self._lib

        lib.shogi_initial_sfen.restype  = ctypes.c_int
        lib.shogi_initial_sfen.argtypes = [ctypes.c_char_p, ctypes.c_int]

        lib.shogi_apply.restype  = ctypes.c_int
        lib.shogi_apply.argtypes = [ctypes.c_char_p, ctypes.c_int,
                                    ctypes.c_char_p, ctypes.c_int]

        lib.shogi_tensor.restype  = ctypes.c_int
        lib.shogi_tensor.argtypes = [ctypes.c_char_p,
                                     ctypes.POINTER(ctypes.c_float)]

        lib.shogi_legal_moves.restype  = ctypes.c_int
        lib.shogi_legal_moves.argtypes = [ctypes.c_char_p,
                                          ctypes.POINTER(ctypes.c_int),
                                          ctypes.c_int]

        # ownership（KataGo 補助タスク）
        lib.shogi_ownership.restype  = ctypes.c_int
        lib.shogi_ownership.argtypes = [ctypes.c_char_p,
                                        ctypes.POINTER(ctypes.c_float)]

        # バッチ API: apply + tensor + legal_moves を 1 回で
        lib.shogi_expand.restype  = ctypes.c_int
        lib.shogi_expand.argtypes = [
            ctypes.c_char_p, ctypes.c_int,   # parent_sfen, move_idx
            ctypes.c_char_p, ctypes.c_int,   # sfen_out, sfen_buf_len
            ctypes.POINTER(ctypes.c_float),  # tensor_out
            ctypes.POINTER(ctypes.c_int), ctypes.c_int  # moves_out, max_moves
        ]

    # ── GameEnv 互換 API ─────────────────────────────────────────

    def initial_sfen(self) -> str:
        n = self._lib.shogi_initial_sfen(self._sfen_buf, self._SFEN_BUF)
        if n < 0: raise RuntimeError("shogi_initial_sfen failed")
        return self._sfen_buf.value.decode()

    def apply(self, sfen: str, move_index: int) -> str:
        """手を適用して新しい SFEN を返す。"""
        r = self._lib.shogi_apply(
            sfen.encode(), move_index, self._sfen_buf, self._SFEN_BUF
        )
        if r == -99: raise RuntimeError("shogi_apply failed")
        return self._sfen_buf.value.decode()

    def is_terminal(self, sfen: str) -> bool:
        n = self._lib.shogi_legal_moves(
            sfen.encode(), self._moves_buf, self._MAX_MOVES
        )
        return n <= 0

    def result(self, sfen: str) -> int:
        """1=先手勝ち, -1=後手勝ち, 0=進行中。"""
        r = self._lib.shogi_apply(
            sfen.encode(), 0, self._sfen_buf, self._SFEN_BUF  # dummy move
        )
        # より簡単な方法: legal_moves が 0 かどうかで判定
        n = self._lib.shogi_legal_moves(
            sfen.encode(), self._moves_buf, self._MAX_MOVES
        )
        if n > 0: return 0
        # 終局: 手番側が負け
        # 手番を知るには SFEN を解析する必要がある
        return -1 if ' b ' in sfen else 1

    def current_player(self, sfen: str) -> str:
        return "先手" if ' b ' in sfen else "後手"

    def legal_moves(self, sfen: str) -> list[int]:
        n = self._lib.shogi_legal_moves(
            sfen.encode(), self._moves_buf, self._MAX_MOVES
        )
        if n <= 0: return []
        return list(self._moves_buf[:n])

    def to_tensor(self, sfen: str) -> np.ndarray:
        """shape: (47, 9, 9), dtype=float32"""
        r = self._lib.shogi_tensor(sfen.encode(), self._tensor_buf)
        if r < 0: raise RuntimeError("shogi_tensor failed")
        return np.frombuffer(self._tensor_buf, dtype=np.float32).copy().reshape(
            self.TENSOR_CHANNELS, self.BOARD_SIZE, self.BOARD_SIZE
        )

    def ownership(self, sfen: str) -> np.ndarray:
        """各升の帰属を返す。shape: (81,), 先手=+1, 後手=-1, 互角=0"""
        self._lib.shogi_ownership(sfen.encode(), self._ownership_buf)
        return np.frombuffer(self._ownership_buf, dtype=np.float32).copy()

    # ── バッチ API（batched_mcts 向け最適化）────────────────────

    def expand(self, parent_sfen: str, move_idx: int
               ) -> tuple[str, np.ndarray | None, list[int] | None]:
        """
        apply + tensor + legal_moves を 1 回のネイティブ呼び出しで実行。
        戻り値: (new_sfen, tensor(47,9,9) or None, legal_moves or None)
        terminal のとき tensor と legal_moves は None。
        """
        n = self._lib.shogi_expand(
            parent_sfen.encode(), move_idx,
            self._sfen_buf, self._SFEN_BUF,
            self._tensor_buf,
            self._moves_buf, self._MAX_MOVES
        )
        new_sfen = self._sfen_buf.value.decode()

        if n < 0:   # terminal または error
            return new_sfen, None, None

        tensor = np.frombuffer(self._tensor_buf, dtype=np.float32).copy().reshape(
            self.TENSOR_CHANNELS, self.BOARD_SIZE, self.BOARD_SIZE
        )
        moves = list(self._moves_buf[:n])
        return new_sfen, tensor, moves
