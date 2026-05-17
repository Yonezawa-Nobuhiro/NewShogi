"""
Human-in-the-Loop 学習サイクル

使い方:
  1. WPF アプリで対局して勝った棋譜を ml/kifu/ に保存
  2. python train_cycle.py を実行
  3. checkpoints/model_cycle_NNNN.onnx が生成される
  4. WPF アプリで読み込んで対局 → また勝って保存 → 2 に戻る

kifu/ フォルダの全 .kf ファイルが累積で取り込まれる。
新しい棋譜も古い棋譜も残しておいてよい（データが増えるほど強くなる）。
"""

import pathlib, glob, torch
from train import run, HP
from native_game_env import NativeGameEnv
from self_play import PrioritizedReplayBuffer
from kifu_loader import load_all_kifu_in_dir
from network import build_net

# ── サイクル学習のハイパーパラメータ ──────────────────────────────
HP_CYCLE = dict(
    num_sims        = 32,
    mcts_batch_size = 2,
    games_per_iter  = 2,    # 自己対局は最小限（棋譜が主役）
    train_steps     = 200,
    num_iters       = 1,    # 1 サイクル = 1 iter
    min_buffer      = 100,
    save_every      = 1,
    checkpoint_dir  = "checkpoints",
    buffer_size     = 50_000,
    per_alpha       = 0.6,
    per_beta        = 0.4,
)

KIFU_DIR  = pathlib.Path(__file__).parent / "kifu"
CKPT_DIR  = pathlib.Path(HP_CYCLE["checkpoint_dir"])
KIFU_REPEAT = 5   # 棋譜サンプルを何回繰り返してバッファに追加するか


def find_latest_model() -> str | None:
    """最新のチェックポイントを探す（cycle > final > iter の順）"""
    candidates = sorted(CKPT_DIR.glob("model_cycle_*.pt"), reverse=True)
    if candidates:
        return str(candidates[0])
    final = CKPT_DIR / "model_final.pt"
    if final.exists():
        return str(final)
    iters = sorted(
        [p for p in glob.glob(str(CKPT_DIR / "model_iter*.pt"))
         if "_v1_" not in p],
        reverse=True
    )
    return iters[0] if iters else None


def export_onnx(model_path: str) -> str:
    """指定モデルを ONNX にエクスポートして パスを返す。"""
    net, device = build_net()
    net.load(model_path, torch.device("cpu"))
    net.eval()
    onnx_path = str(pathlib.Path(model_path).with_suffix(".onnx"))
    dummy = torch.zeros(1, 47, 9, 9)
    torch.onnx.export(
        net.cpu(), dummy, onnx_path,
        input_names=["input"], output_names=["policy", "value"],
        dynamic_axes={"input": {0: "batch_size"}},
        opset_version=17,
        dynamo=False,
    )
    return onnx_path


def main():
    CKPT_DIR.mkdir(exist_ok=True)
    KIFU_DIR.mkdir(exist_ok=True)

    # ── サイクル番号を決定 ──────────────────────────────────────────
    existing = sorted(CKPT_DIR.glob("model_cycle_*.pt"), reverse=True)
    cycle_no = int(existing[0].stem.split("_")[-1]) + 1 if existing else 1
    print(f"\n{'='*50}")
    print(f"  Human-in-the-Loop サイクル #{cycle_no}")
    print(f"{'='*50}")

    # ── 棋譜をバッファに取り込む ────────────────────────────────────
    kifu_files = list(KIFU_DIR.glob("*.kf"))
    if not kifu_files:
        print(f"\n⚠  kifu/ フォルダに .kf ファイルがありません。")
        print("   WPF アプリで対局して棋譜を保存してから再実行してください。")
        return

    print(f"\n棋譜: {len(kifu_files)} 件 (repeat={KIFU_REPEAT})")
    env = NativeGameEnv()
    kifu_buf = PrioritizedReplayBuffer(
        max_size=HP_CYCLE["buffer_size"],
        alpha=HP_CYCLE["per_alpha"],
        beta=HP_CYCLE["per_beta"])
    load_all_kifu_in_dir(str(KIFU_DIR), env, kifu_buf, repeat=KIFU_REPEAT)
    print(f"初期バッファ: {len(kifu_buf):,} サンプル")

    # ── 継続学習のベースモデルを探す ────────────────────────────────
    pretrained = find_latest_model()
    print(f"ベースモデル: {pathlib.Path(pretrained).name if pretrained else '新規'}")

    # ── 1 イテレーション学習 ─────────────────────────────────────────
    net = run(HP_CYCLE, pretrained_path=pretrained, initial_buffer=kifu_buf)

    # ── サイクルモデルとして保存 ────────────────────────────────────
    cycle_pt   = CKPT_DIR / f"model_cycle_{cycle_no:04d}.pt"
    cycle_onnx = cycle_pt.with_suffix(".onnx")
    net.save(cycle_pt)
    print(f"\nモデル保存: {cycle_pt.name}")

    # ── ONNX エクスポート ────────────────────────────────────────────
    onnx_path = export_onnx(str(cycle_pt))
    print(f"ONNX 出力:  {pathlib.Path(onnx_path).name}")
    print(f"\nWPF アプリで {pathlib.Path(onnx_path).name} を読み込んで対局してください。")
    print(f"勝ったら棋譜を kifu/ に保存して、再度 train_cycle.py を実行！")


if __name__ == "__main__":
    main()
