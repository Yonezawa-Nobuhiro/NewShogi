"""
NNUE 学習スクリプト。
Tuner の eval_data モードで生成した (SFEN, score) TSV を学習し、
C# で読み込めるバイナリ重みファイル (nnue_weights.bin) を出力する。

使い方:
  # 1. 学習データ生成（Windows）
  dotnet run --project 変成将棋.Tuner -c Release -- eval_data \\
      変成将棋.AI/αβパラメータ.json 2000 ml/checkpoints/eval_data.tsv

  # 2. NNUE 学習
  python ml/tools/train_nnue.py \\
      --data ml/checkpoints/eval_data.tsv \\
      --out  変成将棋.AI/nnue_weights.bin

特徴量 (2606次元):
  Board: 先手/後手 × 16駒種 × 81升 = 2592 (sparse binary)
  Hand : 先手/後手 × 7種      = 14   (枚数/10)

ネットワーク: 2606 → 256 (ReLU) → 64 (ReLU) → 1
"""

import argparse, re, struct, sys
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn

# ── 定数 ──────────────────────────────────────────────────────────────────────

FEATURE_SIZE = 2_606
L1, L2 = 256, 64
BOARD_BASE = 0
HAND_BASE  = 2 * 16 * 81  # 2592

# E駒種 の整数値 (なし=0, 歩兵=1..獅王=16)
PIECE_NAMES = ["なし","歩兵","香車","桂馬","銀将","金将","角行","飛車","玉将",
               "と金","竪行","騎兵","麒麟","鳳凰","龍馬","龍王","獅王"]
# 持ち駒になり得る駒種インデックス（0-origin: 歩兵=0…飛車=6）
HAND_PIECES_IDX = [1,2,3,4,5,6,7]  # 歩兵～飛車 の E駒種 値

# SFEN の駒文字 → E駒種 値
_SFEN_MAP = {
    'P':1,'L':2,'N':3,'S':4,'G':5,'B':6,'R':7,'K':8,
    '+P':9,'+L':10,'+N':11,'+S':12,'+G':13,'+B':14,'+R':15,'+K':16,
}
# 成り前の駒種（持ち駒用）
_UNPROMO = {9:1,10:2,11:3,12:4,13:5,14:6,15:7,16:8}


# ── SFEN → 特徴ベクトル ───────────────────────────────────────────────────────

def sfen_to_features(sfen: str) -> np.ndarray:
    """SFEN を float32 特徴ベクトル (FEATURE_SIZE,) に変換する。"""
    parts = sfen.split()
    board_str = parts[0]
    turn_str  = parts[1] if len(parts) > 1 else 'b'
    hand_str  = parts[2] if len(parts) > 2 else '-'

    feat = np.zeros(FEATURE_SIZE, dtype=np.float32)

    # ── 盤面 ────────────────────────────────────────────────────────────────
    sq = 0  # 0-80, row-major (9*row + col), 行は段-1, 列は列-1
    i = 0
    while i < len(board_str) and sq < 81:
        c = board_str[i]
        if c == '/':
            i += 1
            continue
        if c.isdigit():
            sq += int(c)
            i += 1
            continue

        # 成り駒
        promoted = False
        if c == '+':
            promoted = True
            i += 1
            c = board_str[i]

        player = 0 if c.isupper() else 1
        key = ('+' if promoted else '') + c.upper()
        piece = _SFEN_MAP.get(key, 0)
        if piece > 0:
            fidx = BOARD_BASE + player * 16 * 81 + (piece - 1) * 81 + sq
            feat[fidx] = 1.0
        sq += 1
        i += 1

    # ── 持ち駒 ──────────────────────────────────────────────────────────────
    if hand_str != '-':
        count = 1
        hi = 0
        while hi < len(hand_str):
            c = hand_str[hi]
            if c.isdigit():
                # 複数枚
                num = 0
                while hi < len(hand_str) and hand_str[hi].isdigit():
                    num = num * 10 + int(hand_str[hi])
                    hi += 1
                count = num
                continue
            player = 0 if c.isupper() else 1
            piece  = _SFEN_MAP.get(c.upper(), 0)
            unp    = _UNPROMO.get(piece, piece)  # 成り前に戻す
            if unp in HAND_PIECES_IDX:
                hand_pos = HAND_PIECES_IDX.index(unp)
                fidx = HAND_BASE + player * 7 + hand_pos
                feat[fidx] = count / 10.0
            count = 1
            hi += 1

    return feat


# ── ネットワーク ──────────────────────────────────────────────────────────────

class NNUE(nn.Module):
    def __init__(self):
        super().__init__()
        self.l1 = nn.Linear(FEATURE_SIZE, L1)
        self.l2 = nn.Linear(L1, L2)
        self.out = nn.Linear(L2, 1)

    def forward(self, x):
        x = torch.relu(self.l1(x))
        x = torch.relu(self.l2(x))
        return self.out(x).squeeze(-1)


# ── データロード ──────────────────────────────────────────────────────────────

def load_data(tsv_path: str, max_score: int = 3000):
    """TSV を読み込んで (features, targets) を返す。"""
    sfens, scores = [], []
    with open(tsv_path, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split('\t')
            if len(parts) < 2:
                continue
            try:
                score = int(parts[1])
            except ValueError:
                continue
            # 極端なスコアを除外（詰みスコアなど）
            if abs(score) > max_score:
                continue
            sfens.append(parts[0])
            scores.append(score / 2000.0)  # [-1, 1] に正規化

    print(f"  {len(sfens):,} サンプルをロード")
    print("  特徴量変換中...", end='', flush=True)
    X = np.array([sfen_to_features(s) for s in sfens], dtype=np.float32)
    y = np.array(scores, dtype=np.float32)
    print(" 完了")
    return X, y


# ── 重みエクスポート ──────────────────────────────────────────────────────────

def export_weights(model: NNUE, out_path: str):
    """C# CNNUE評価器 が読み込めるバイナリ形式で出力する。"""
    with open(out_path, 'wb') as f:
        def write_array(arr: np.ndarray):
            f.write(arr.astype(np.float32).tobytes())

        sd = model.state_dict()
        # W1: [L1, FEATURE_SIZE] → [FEATURE_SIZE, L1] に転置（C# では feat*L1 + j）
        write_array(sd['l1.weight'].cpu().numpy().T.flatten())
        write_array(sd['l1.bias'].cpu().numpy())
        write_array(sd['l2.weight'].cpu().numpy().T.flatten())
        write_array(sd['l2.bias'].cpu().numpy())
        write_array(sd['out.weight'].cpu().numpy().flatten())
        f.write(struct.pack('f', sd['out.bias'].cpu().numpy().item()))
    print(f"  重みを保存: {out_path}")


# ── 学習 ─────────────────────────────────────────────────────────────────────

def train(data_path: str, out_path: str, epochs: int = 30, batch: int = 2048, lr: float = 1e-3):
    device = torch.device('cpu')  # GTX 1060 (sm_61) は PyTorch CUDA 非対応
    print(f"device={device}")

    X, y = load_data(data_path)
    n = len(X)
    idx = np.random.permutation(n)
    split = int(n * 0.9)
    X_tr, y_tr = X[idx[:split]], y[idx[:split]]
    X_va, y_va = X[idx[split:]], y[idx[split:]]

    X_tr = torch.from_numpy(X_tr).to(device)
    y_tr = torch.from_numpy(y_tr).to(device)
    X_va = torch.from_numpy(X_va).to(device)
    y_va = torch.from_numpy(y_va).to(device)

    model = NNUE().to(device)
    opt   = torch.optim.Adam(model.parameters(), lr=lr, weight_decay=1e-5)
    sched = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=epochs)
    loss_fn = nn.MSELoss()

    print(f"\n{'epoch':>5}  {'train_loss':>10}  {'val_loss':>10}")
    print('-' * 30)

    best_val = float('inf')
    for ep in range(1, epochs + 1):
        model.train()
        perm = torch.randperm(len(X_tr), device=device)
        tr_loss = 0.0
        for start in range(0, len(X_tr), batch):
            bi = perm[start:start+batch]
            pred = model(X_tr[bi])
            loss = loss_fn(pred, y_tr[bi])
            opt.zero_grad()
            loss.backward()
            opt.step()
            tr_loss += loss.item() * len(bi)
        tr_loss /= len(X_tr)

        model.eval()
        with torch.no_grad():
            va_loss = loss_fn(model(X_va), y_va).item()

        sched.step()
        print(f"{ep:>5}  {tr_loss:>10.5f}  {va_loss:>10.5f}")

        if va_loss < best_val:
            best_val = va_loss
            export_weights(model, out_path)

    print(f"\n最良 val_loss: {best_val:.5f}")
    print(f"出力: {out_path}")


# ── エントリポイント ──────────────────────────────────────────────────────────

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='NNUE 学習')
    parser.add_argument('--data',   required=True, help='eval_data.tsv のパス')
    parser.add_argument('--out',    required=True, help='nnue_weights.bin の出力先')
    parser.add_argument('--epochs', type=int, default=30)
    parser.add_argument('--batch',  type=int, default=2048)
    parser.add_argument('--lr',     type=float, default=1e-3)
    args = parser.parse_args()

    train(args.data, args.out, args.epochs, args.batch, args.lr)
