"""
NNUE 自己学習ループ

  世代 N:
    1. dotnet Tuner (αβ探索) で局面スコアデータを生成
    2. train_nnue_halfkp.py で NNUE を学習（前世代からファインチューン）
    3. 重みを 変成将棋.AI/ に配置して次世代へ

使い方 (プロジェクトルートから):
  python ml/tools/nnue_selflearn.py
  python ml/tools/nnue_selflearn.py --iters 5 --games 500 --epochs 20
  python ml/tools/nnue_selflearn.py --iters 1 --games 50 --epochs 5 --dry-run
"""

import argparse
import subprocess
import sys
import shutil
import time
import json
from pathlib import Path
from datetime import datetime

# ── パス設定 ─────────────────────────────────────────────────────────────────
ROOT       = Path(__file__).resolve().parents[2]   # プロジェクトルート
TUNER_PROJ = ROOT / "変成将棋.Tuner"
PARAMS     = ROOT / "変成将棋.AI" / "αβパラメータ.json"
WEIGHTS      = ROOT / "変成将棋.AI" / "nnue_weights_halfkp.bin"
WEIGHTS_INT8 = ROOT / "変成将棋.AI" / "nnue_weights_halfkp_i8.bin"
RUNS_DIR   = ROOT / "ml" / "checkpoints" / "runs"
TRAIN_SCRIPT = Path(__file__).parent / "train_nnue_halfkp.py"

# ── ヘルパー ──────────────────────────────────────────────────────────────────

def run(cmd: list[str], label: str) -> tuple[float, bool]:
    """コマンドを実行して (経過秒, 成功か) を返す。"""
    print(f"\n  [{label}] $ {' '.join(str(c) for c in cmd)}")
    t0 = time.perf_counter()
    r = subprocess.run(cmd, cwd=ROOT)
    elapsed = time.perf_counter() - t0
    ok = (r.returncode == 0)
    status = "OK" if ok else f"FAILED (code={r.returncode})"
    print(f"  [{label}] {elapsed:.1f}s  {status}")
    return elapsed, ok


def gen_num() -> int:
    """既存チェックポイントから次の世代番号を決める。"""
    existing = sorted(RUNS_DIR.glob("gen_*/"))
    if not existing:
        return 1
    return int(existing[-1].name.split("_")[1]) + 1


# ── メイン ────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="NNUE 自己学習ループ")
    ap.add_argument("--iters",  type=int, default=3,    help="反復回数")
    ap.add_argument("--games",  type=int, default=200,  help="1世代あたりの対局数")
    ap.add_argument("--epochs", type=int, default=20,   help="1世代あたりの学習エポック数")
    ap.add_argument("--batch",  type=int, default=4096, help="ミニバッチサイズ")
    ap.add_argument("--lr",     type=float, default=5e-4, help="学習率")
    ap.add_argument("--dry-run", action="store_true",   help="コマンドを表示するだけで実行しない")
    args = ap.parse_args()

    RUNS_DIR.mkdir(parents=True, exist_ok=True)

    log_entries = []
    start_gen = gen_num()

    print(f"\n{'='*60}")
    print(f"  NNUE 自己学習ループ  ({args.iters} 世代, {args.games} 局/世代)")
    print(f"  開始世代: {start_gen}")
    print(f"{'='*60}")

    for it in range(args.iters):
        gen = start_gen + it
        gen_dir = RUNS_DIR / f"gen_{gen:04d}"
        gen_dir.mkdir(exist_ok=True)

        data_tsv        = gen_dir / "eval_data.tsv"
        weights_out     = gen_dir / "nnue_weights_halfkp.bin"
        weights_int8_out = gen_dir / "nnue_weights_halfkp_i8.bin"
        seed = gen * 137  # 世代ごとに異なるシード

        banner = f"世代 {gen} / {start_gen + args.iters - 1}"
        print(f"\n{'─'*60}")
        print(f"  {banner}   ({datetime.now().strftime('%H:%M:%S')})")
        print(f"{'─'*60}")

        entry = {"gen": gen, "timestamp": datetime.now().isoformat()}

        # ── Step 1: データ生成 ──────────────────────────────────────────────
        cmd_data = [
            "dotnet", "run",
            "--project", str(TUNER_PROJ),
            "-c", "Release",
            "--",
            "eval_data",
            str(PARAMS),
            str(args.games),
            str(data_tsv),
            str(seed),
        ]
        if args.dry_run:
            print(f"  [DRY] {' '.join(str(c) for c in cmd_data)}")
            t_data = 0.0
        else:
            t_data, ok = run(cmd_data, "データ生成")
            if not ok:
                print("  データ生成に失敗しました。中断します。")
                break
            n_lines = sum(1 for _ in open(data_tsv, encoding="utf-8")) if data_tsv.exists() else 0
            print(f"  サンプル数: {n_lines:,}")
            entry["samples"] = n_lines

        entry["t_data_sec"] = round(t_data, 1)

        # ── Step 2: NNUE 学習 ──────────────────────────────────────────────
        cmd_train = [
            sys.executable, "-u",
            str(TRAIN_SCRIPT),
            "--data",   str(data_tsv),
            "--out",    str(weights_out),
            "--epochs", str(args.epochs),
            "--batch",  str(args.batch),
            "--lr",     str(args.lr),
        ]
        # 前世代の重みがあればファインチューン
        if WEIGHTS.exists():
            cmd_train += ["--resume", str(WEIGHTS)]
        cmd_train += ["--export-int8", str(weights_int8_out)]

        if args.dry_run:
            print(f"  [DRY] {' '.join(str(c) for c in cmd_train)}")
            t_train = 0.0
        else:
            t_train, ok = run(cmd_train, "NNUE 学習")
            if not ok:
                print("  学習に失敗しました。中断します。")
                break

        entry["t_train_sec"] = round(t_train, 1)

        # ── Step 3: 重みを配置 ─────────────────────────────────────────────
        if not args.dry_run and weights_out.exists():
            shutil.copy2(weights_out, WEIGHTS)
            size_kb = WEIGHTS.stat().st_size // 1024
            print(f"\n  重みを更新: {WEIGHTS.name}  ({size_kb:,} KB)")
            if weights_int8_out.exists():
                shutil.copy2(weights_int8_out, WEIGHTS_INT8)
                size_kb8 = WEIGHTS_INT8.stat().st_size // 1024
                print(f"  INT8 重みを更新: {WEIGHTS_INT8.name}  ({size_kb8:,} KB)")
        elif args.dry_run:
            print(f"  [DRY] copy {weights_out} → {WEIGHTS}")
            print(f"  [DRY] copy {weights_int8_out} → {WEIGHTS_INT8}")

        t_total = t_data + t_train
        entry["t_total_sec"] = round(t_total, 1)
        log_entries.append(entry)

        print(f"\n  世代 {gen} 完了  データ生成={t_data:.0f}s  学習={t_train:.0f}s  合計={t_total:.0f}s")

    # ── ログ保存 ──────────────────────────────────────────────────────────────
    if not args.dry_run and log_entries:
        log_path = RUNS_DIR / "selflearn_log.json"
        existing_log = []
        if log_path.exists():
            try:
                existing_log = json.loads(log_path.read_text(encoding="utf-8"))
            except Exception:
                pass
        log_path.write_text(
            json.dumps(existing_log + log_entries, ensure_ascii=False, indent=2),
            encoding="utf-8"
        )
        print(f"\nログ保存: {log_path}")

    print(f"\n{'='*60}")
    print("  自己学習ループ完了")
    if log_entries:
        avg_data  = sum(e["t_data_sec"]  for e in log_entries) / len(log_entries)
        avg_train = sum(e["t_train_sec"] for e in log_entries) / len(log_entries)
        print(f"  平均 データ生成: {avg_data:.0f}s  学習: {avg_train:.0f}s  / 世代")
    print(f"{'='*60}\n")


if __name__ == "__main__":
    main()
