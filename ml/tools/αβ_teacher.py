"""
αβ AI（探索深さ3）を教師にしたニューラルネットワーク学習ループ。

ランダムAIの代わりに αβ depth 3 でゲームを生成することで、
鶏卵問題を回避しながら良質な学習データを確保する。

フロー（1イテレーション）:
  1. αβ depth 3 で games_per_iter 局を parallel 並列生成 → .kf ファイル
  2. kifu_loader でサンプルに変換 → PrioritizedReplayBuffer
  3. train_step で batch_size 局 × train_steps 回勾配更新
  4. チェックポイント保存 / ログ追記

使い方:
  python ml/tools/αβ_teacher.py --iters 50 --games 60 --train_steps 500
  python ml/tools/αβ_teacher.py --resume      # 前回から継続

注意: 現状 Windows 専用 (変成将棋.Tuner が net8.0-windows ビルド)
"""

import argparse, json, os, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

# ── パス設定 ──────────────────────────────────────────────────────────────────
SCRIPT_DIR  = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent.parent
ML_DIR      = PROJECT_DIR / "ml"
sys.path.insert(0, str(ML_DIR))

TUNER_DLL  = PROJECT_DIR / "変成将棋.Tuner/bin/Release/net8.0-windows/変成将棋.Tuner.dll"
BASE_PARAMS = PROJECT_DIR / "変成将棋.AI/αβパラメータ.json"
CKPT_DIR   = PROJECT_DIR / "ml/checkpoints"
KIFU_DIR   = CKPT_DIR / "teacher_kifu"
LOG_PATH   = CKPT_DIR / "αβ_teacher_log.json"
STATE_PATH = CKPT_DIR / "αβ_teacher_state.json"

# 学習用探索深さ（ゲーム生成専用。本番対局の αβパラメータ.json とは独立）
TRAIN_DEPTH = 3


# ── ゲーム生成 ────────────────────────────────────────────────────────────────

def _make_depth3_params() -> str:
    """αβパラメータ.json を深さ3に上書きした一時ファイルを返す"""
    import tempfile, json
    with open(BASE_PARAMS, encoding='utf-8') as f:
        p = json.load(f)
    p["探索深さ"] = TRAIN_DEPTH
    fd, path = tempfile.mkstemp(suffix='.json')
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        json.dump(p, f, ensure_ascii=False)
    return path


def _generate_batch(args_tuple):
    """1プロセス分のゲーム生成（ThreadPoolExecutor から呼ぶ）"""
    params, n, out_dir, seed = args_tuple
    cmd = ["dotnet", str(TUNER_DLL), "generate", params, str(n), out_dir, str(seed)]
    # C# の stderr は日本語 UTF-8。text=True はシステムエンコード(cp932)で失敗するので
    # バイナリで受け取り戻り値だけ確認する。
    r = subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                       timeout=3600)
    return r.returncode == 0


def generate_games(n_games: int, parallel: int, out_dir: str, seed_base: int):
    """parallel 並列で合計 n_games 局を生成"""
    os.makedirs(out_dir, exist_ok=True)
    params = _make_depth3_params()
    try:
        per  = max(1, n_games // parallel)
        last = n_games - per * (parallel - 1)
        tasks = [(params, per if i < parallel - 1 else last, out_dir, seed_base + i * 1000)
                 for i in range(parallel)]
        with ThreadPoolExecutor(max_workers=parallel) as ex:
            list(ex.map(_generate_batch, tasks))
    finally:
        os.unlink(params)


# ── 学習 ──────────────────────────────────────────────────────────────────────

def load_and_train(kifu_dir: str, net, optimizer, device,  # noqa: ANN001
                   batch_size: int, train_steps: int,
                   max_buffer: int = 200_000) -> dict:
    """
    kifu_dir の .kf を読み込んでバッファを構築し train_steps 回更新する。
    Returns: 平均損失の辞書
    """
    from self_play import PrioritizedReplayBuffer
    from kifu_loader import load_kifu_to_buffer
    from train import train_step

    try:
        from native_game_env import NativeGameEnv
        env = NativeGameEnv()
    except Exception:
        import numpy as _np
        from game_env import GameEnv  # type: ignore
        _base = GameEnv()
        # GameEnv に ownership スタブを追加（ゼロベクトル = ownership 情報なし）
        _base.ownership = lambda sfen: _np.zeros(81, dtype=_np.float32)  # type: ignore
        env = _base
    buf = PrioritizedReplayBuffer(max_size=max_buffer)

    kf_files = sorted(Path(kifu_dir).glob("*.kf"))
    if not kf_files:
        return {}

    for kf in kf_files:
        try:
            load_kifu_to_buffer(str(kf), env, buf)
        except Exception as e:
            print(f"  警告: {kf.name} スキップ ({e})")

    if len(buf._buf) < batch_size:
        print(f"  サンプル不足 ({len(buf._buf)} < {batch_size})")
        return {}

    losses = {"policy": [], "value": [], "own": [], "vp": []}
    for _ in range(train_steps):
        tensors, policies, values, vprefixes, ownerships, weights, indices = \
            buf.sample(batch_size)
        p_loss, v_loss, own_loss, vp_loss, _, errs = train_step(
            net, optimizer, tensors, policies, values, vprefixes,
            ownerships, weights, device)
        buf.update_priorities(indices, errs)
        losses["policy"].append(p_loss)
        losses["value"].append(v_loss)
        losses["own"].append(own_loss)
        losses["vp"].append(vp_loss)

    def avg(lst): return round(sum(lst) / len(lst), 4) if lst else 0
    return {k: avg(v) for k, v in losses.items()}


# ── メインループ ──────────────────────────────────────────────────────────────

def run(args):
    import torch
    from network import build_net

    CKPT_DIR.mkdir(parents=True, exist_ok=True)

    # ─ 状態の復元 or 新規 ─────────────────────────────────────────────────────
    if args.resume and STATE_PATH.exists():
        state = json.loads(STATE_PATH.read_text(encoding='utf-8'))
        start_iter = state["iter"]
        log = state.get("log", [])
        print(f"  → iter {start_iter} から再開")
    else:
        start_iter = 0
        log = []

    # ─ モデルの構築・ロード ────────────────────────────────────────────────────
    # build_net は (net, device) タプルを返す
    net, device = build_net(num_blocks=10, channels=128)
    print(f"[αβ教師学習] device={device}, depth={TRAIN_DEPTH}")
    optimizer = torch.optim.Adam(net.parameters(), lr=1e-3, weight_decay=1e-4)

    # 最新チェックポイントを探す
    import glob as _glob, re
    pts = sorted(_glob.glob(str(CKPT_DIR / "model_iter*.pt")))
    if pts:
        ckpt = pts[-1]
        net.load_state_dict(torch.load(ckpt, map_location=device))
        print(f"  → モデルロード: {Path(ckpt).name}")
    elif (CKPT_DIR / "model_final.pt").exists():
        net.load_state_dict(torch.load(CKPT_DIR / "model_final.pt", map_location=device))
        print(f"  → モデルロード: model_final.pt")
    else:
        print(f"  → 新規モデルで開始")

    # ─ 学習ループ ───────────────────────────────────────────────────────────────
    total = start_iter + args.iters
    print(f"\n{'iter':>5}  {'elapsed':>7}  {'p_loss':>7}  {'v_loss':>7}  {'games':>6}")
    print("-" * 45)

    t_start = time.time()

    for it in range(start_iter + 1, total + 1):
        t0 = time.time()

        # 棋譜ディレクトリをイテレーションごとにリセット
        kifu_dir = str(KIFU_DIR / f"iter{it:04d}")
        for f in Path(kifu_dir).glob("*.kf") if Path(kifu_dir).exists() else []:
            f.unlink()

        # 1. ゲーム生成
        generate_games(args.games, args.parallel, kifu_dir, it * 10000)
        kf_count = len(list(Path(kifu_dir).glob("*.kf")))

        # 2. 学習
        losses = load_and_train(
            kifu_dir, net, optimizer, device,
            args.batch_size, args.train_steps)

        elapsed = time.time() - t_start
        entry = {
            "iter": it, "games": kf_count, "elapsed": round(elapsed, 1),
            **losses
        }
        log.append(entry)

        p_loss = losses.get("policy", 0)
        v_loss = losses.get("value", 0)
        print(f"{it:>5}  {elapsed:>6.0f}s  {p_loss:>7.4f}  {v_loss:>7.4f}  {kf_count:>6}")

        # 3. 保存
        if it % args.save_every == 0 or it == total:
            ckpt_path = CKPT_DIR / f"model_iter{it:04d}.pt"
            torch.save(net.state_dict(), str(ckpt_path))
            STATE_PATH.write_text(
                json.dumps({"iter": it, "log": log}, ensure_ascii=False, indent=2),
                encoding='utf-8')
            LOG_PATH.write_text(
                json.dumps(log, ensure_ascii=False, indent=2),
                encoding='utf-8')
            print(f"  → 保存: {ckpt_path.name}")

    print(f"\n完了: {total} iter")
    return net


# ── エントリポイント ──────────────────────────────────────────────────────────

if __name__ == "__main__":
    if not TUNER_DLL.exists():
        print("[ERROR] Tuner が見つかりません。ビルドしてください:")
        print(f"  dotnet build 変成将棋.Tuner -c Release")
        sys.exit(1)

    parser = argparse.ArgumentParser(description="αβ教師学習")
    parser.add_argument("--iters",       type=int, default=50,   help="学習イテレーション数")
    parser.add_argument("--games",       type=int, default=60,   help="1iter あたりのゲーム数")
    parser.add_argument("--train_steps", type=int, default=500,  help="1iter あたりの勾配更新回数")
    parser.add_argument("--batch_size",  type=int, default=256,  help="バッチサイズ")
    parser.add_argument("--parallel",    type=int, default=6,    help="並列ゲーム生成数")
    parser.add_argument("--save_every",  type=int, default=5,    help="何iterごとに保存するか")
    parser.add_argument("--resume",      action="store_true",    help="前回から継続")
    args = parser.parse_args()

    run(args)
