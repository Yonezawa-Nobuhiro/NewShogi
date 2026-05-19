"""
αβ自己対局棋譜を大量生成して teacher_kifu/iter####/ に保存する。
Colab で学習する前に Windows 側で実行するスクリプト。

使い方:
  python ml/tools/generate_teacher_games.py --iters 50 --games 60 --parallel 6
  python ml/tools/generate_teacher_games.py --iters 50 --games 60 --parallel 6 --start 51  # 継続

出力先: ml/checkpoints/teacher_kifu/iter0001/ ~ iter0050/
各 iter に depth=3 の αβ自己対局棋譜 (.kf) が games 個入る。
"""

import argparse, json, os, subprocess, tempfile, time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

PROJECT_DIR = Path(__file__).resolve().parent.parent.parent
TUNER_DLL   = PROJECT_DIR / "変成将棋.Tuner/bin/Release/net8.0-windows/変成将棋.Tuner.dll"
BASE_PARAMS = PROJECT_DIR / "変成将棋.AI/αβパラメータ.json"
KIFU_BASE   = PROJECT_DIR / "ml/checkpoints/teacher_kifu"
TRAIN_DEPTH = 3  # ゲーム生成専用深さ（本番の αβパラメータ.json とは独立）


def _make_params(depth: int) -> str:
    with open(BASE_PARAMS, encoding='utf-8') as f:
        p = json.load(f)
    p['探索深さ'] = depth
    fd, path = tempfile.mkstemp(suffix='.json')
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        json.dump(p, f, ensure_ascii=False)
    return path


def _run_batch(args_tuple):
    params, n_games, out_dir, seed = args_tuple
    cmd = ["dotnet", str(TUNER_DLL), "generate",
           params, str(n_games), out_dir, str(seed)]
    subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                   timeout=7200)


def generate_iter(iter_num: int, games: int, parallel: int) -> int:
    """1 iter 分の棋譜を生成。生成された .kf ファイル数を返す。"""
    out_dir = str(KIFU_BASE / f"iter{iter_num:04d}")
    os.makedirs(out_dir, exist_ok=True)
    params = _make_params(TRAIN_DEPTH)
    try:
        per  = max(1, games // parallel)
        last = games - per * (parallel - 1)
        tasks = [
            (params, per if i < parallel - 1 else last,
             out_dir, iter_num * 10000 + i * 1000)
            for i in range(parallel)
            if (per if i < parallel - 1 else last) > 0
        ]
        with ThreadPoolExecutor(max_workers=parallel) as ex:
            list(ex.map(_run_batch, tasks))
    finally:
        os.unlink(params)
    return len(list(Path(out_dir).glob('*.kf')))


if __name__ == "__main__":
    if not TUNER_DLL.exists():
        print("[ERROR] Tuner DLL が見つかりません。先にビルドを:")
        print("  dotnet build 変成将棋.Tuner -c Release")
        raise SystemExit(1)

    parser = argparse.ArgumentParser(description="αβ教師棋譜生成")
    parser.add_argument("--iters",    type=int, default=50, help="生成するiter数")
    parser.add_argument("--games",    type=int, default=60, help="1iterあたりのゲーム数")
    parser.add_argument("--parallel", type=int, default=6,  help="並列数（コア数以下に）")
    parser.add_argument("--start",    type=int, default=1,  help="開始iter番号（継続時に指定）")
    args = parser.parse_args()

    end = args.start + args.iters - 1
    total_games = args.iters * args.games
    print(f"αβ棋譜生成: iter {args.start:04d} 〜 {end:04d}")
    print(f"  {args.games}局/iter × {args.iters}iter = {total_games}局合計")
    print(f"  並列: {args.parallel}  depth: {TRAIN_DEPTH}")
    print(f"  出力先: {KIFU_BASE}\n")

    t_all = time.time()
    for i in range(args.iters):
        it = args.start + i
        t0 = time.time()
        count = generate_iter(it, args.games, args.parallel)
        elapsed = time.time() - t0
        total_elapsed = time.time() - t_all
        remain_iters  = args.iters - i - 1
        eta = remain_iters * elapsed if remain_iters > 0 else 0
        print(f"  iter {it:04d}: {count:3d} kf  "
              f"({elapsed:.0f}s/iter  合計{total_elapsed:.0f}s  残{eta:.0f}s)")

    print(f"\n完了！  {total_games} 局生成")
    print(f"次: Colab を開いて cell 7b を実行してください。")
