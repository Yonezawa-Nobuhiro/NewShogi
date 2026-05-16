"""
学習スクリプト（継続対応）。
既存の model_final.pt があればそこから再開し、ONNX エクスポートまで行う。
python train_quick.py で実行。

アーキテクチャ (num_blocks=10, channels=128) は固定。
環境に合わせて変えてよいパラメータ: num_sims / games_per_iter / num_iters
"""
import warnings
warnings.filterwarnings("ignore")

import torch
from pathlib import Path
from train import run

CKPT_DIR = Path("checkpoints")
FINAL_PT  = CKPT_DIR / "model_final.pt"
ONNX_OUT  = CKPT_DIR / "model.onnx"

# ── 環境に合わせて変更する部分 ────────────────────────────────────
hp = dict(
    num_sims       = 16,    # Gumbel: CPU: 16 / GPU(T4): 64
    games_per_iter = 20,    # CPU: 20 / GPU(T4): 100
    train_steps    = 200,
    num_iters      = 12,
    min_buffer     = 200,
    save_every     = 3,
    checkpoint_dir = str(CKPT_DIR),
)
# ─────────────────────────────────────────────────────────────────

pretrained = str(FINAL_PT) if FINAL_PT.exists() else None
if pretrained:
    print(f"継続学習: {FINAL_PT}")
else:
    print("新規学習（10ブロック / 128チャンネル）")

net = run(hp, pretrained_path=pretrained)

# ── ONNX エクスポート ─────────────────────────────────────────────
dummy = torch.zeros(1, 47, 9, 9)
torch.onnx.export(
    net.cpu(), dummy, str(ONNX_OUT),
    input_names   = ["input"],
    output_names  = ["policy", "value"],
    dynamic_axes  = {"input": {0: "batch_size"}},
    opset_version = 17,
)
print(f"\nONNX エクスポート完了 → {ONNX_OUT.resolve()}")
