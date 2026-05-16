"""
C# エンジン連携の動作確認スクリプト。
python test_env.py で実行する。
"""

import sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
from game_env import GameEnv
import time

def main():
    env = GameEnv()

    # 初期局面
    sfen = env.initial_sfen()
    print(f"初期SFEN: {sfen}")
    print(f"手番: {env.current_player(sfen)}")

    # 合法手数
    moves = env.legal_moves(sfen)
    print(f"合法手数: {len(moves)}")

    # 盤面テンソル
    t = env.to_tensor(sfen)
    print(f"テンソル shape: {t.shape}, dtype: {t.dtype}")

    # ランダム対局
    print("\n--- ランダム対局 ---")
    start = time.time()
    history = env.play_random_game()
    elapsed = time.time() - start
    final = history[-1]
    result = env.result(final)
    winner = "先手" if result == 1 else "後手" if result == -1 else "引き分け"
    print(f"手数: {len(history)-1}  結果: {winner}  経過: {elapsed:.2f}秒")

if __name__ == "__main__":
    main()
