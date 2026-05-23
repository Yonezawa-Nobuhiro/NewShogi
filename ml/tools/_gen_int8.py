"""既存の float32 重みから INT8 量子化ファイルを生成する"""
import sys, struct, numpy as np, torch
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(ROOT / 'ml' / 'tools'))
from train_nnue import NNUE, export_weights_int8

src = ROOT / '変成将棋.AI' / 'nnue_weights.bin'
dst = ROOT / '変成将棋.AI' / 'nnue_weights_int8.bin'

with open(src, 'rb') as f:
    def rf(n): return np.frombuffer(f.read(n * 4), dtype=np.float32).copy()
    w1 = rf(2606 * 256)
    b1 = rf(256)
    w2 = rf(256 * 64)
    b2 = rf(64)
    w3 = rf(64)
    b3 = struct.unpack('f', f.read(4))[0]

sd = {
    'l1.weight': torch.from_numpy(w1.reshape(256, 2606)),
    'l1.bias':   torch.from_numpy(b1),
    'l2.weight': torch.from_numpy(w2.reshape(64, 256)),
    'l2.bias':   torch.from_numpy(b2),
    'out.weight': torch.from_numpy(w3.reshape(1, 64)),
    'out.bias':   torch.tensor([b3]),
}

model = NNUE()
model.load_state_dict(sd)
export_weights_int8(model, str(dst))
print(f'生成完了: {dst.name}  ({dst.stat().st_size // 1024} KB)')
