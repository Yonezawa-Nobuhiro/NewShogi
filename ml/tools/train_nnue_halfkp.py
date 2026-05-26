"""
変成将棋 HalfKP NNUE 学習スクリプト。
L1 を Sparse Embedding Lookup で実装し、Dense Linear より大幅に高速化。

仕様: 仕様/NNUE特徴量.md 参照

使い方:
  # 学習データ生成（既存スクリプト）
  dotnet run --project 変成将棋.Tuner -c Release -- eval_data \
      変成将棋.AI/αβパラメータ.json 5000 ml/checkpoints/eval_data.tsv

  # HalfKP 学習
  python ml/tools/train_nnue_halfkp.py \
      --data ml/checkpoints/eval_data.tsv \
      --out  変成将棋.AI/nnue_weights_halfkp.bin
"""

import argparse, struct, re, sys, time, math
sys.stdout.reconfigure(encoding='utf-8')
sys.stderr.reconfigure(encoding='utf-8')
from pathlib import Path

import numpy as np
import torch
import torch.nn as nn
import torch.nn.functional as F

# ── 特徴量定数 ────────────────────────────────────────────────────────────────

FEATURE_SIZE  = 194_643
L1_SIZE       = 256
L2_SIZE       = 64
BUCKET_COUNT  = 3
MAX_FEATURES  = 50    # 1局面のアクティブ特徴数上限（パディング用）
PAD_IDX       = FEATURE_SIZE  # Embedding の最後の行 = 0 ベクトル（パディング）

# 持駒歩区分: 変成将棋には持将棋ルールがないため歩の積み上げ局面が生じにくく、
# 18枚 one-hot は希少区分が学習不足になる。0〜4・5-9・10以上 の7区分を使用。
HAND_PAWN_BUCKETS = 7

# オフセット（81×2×14×81=183,708 が正しい駒位置サイズ）
PIECE_OFFSET      = 0
EKING_OFFSET      = 183_708
HAND_PAWN_OFFSET  = 190_269   # 81×2×7      =  1,134
HAND_SMALL_OFFSET = 191_403   # 81×2×4×4   =  2,592
HAND_LARGE_OFFSET = 193_995   # 81×2×2×2   =    648

# SFEN 駒文字 → piece_type_idx (0-13)。玉将・獅王は除外。
_SFEN_PIECE_IDX = {
    'P':0, 'L':1, 'N':2, 'S':3, 'G':4, 'B':5, 'R':6,
    '+P':7, '+L':8, '+N':9, '+S':10, '+G':11, '+B':12, '+R':13,
}
# 小駒（持駒カテゴリ）: 香(1),桂(2),銀(3),金(4) の SFEN 文字 → small_idx
_SMALL_PIECE_IDX = {'L':0, 'N':1, 'S':2, 'G':3}
# 大駒（持駒カテゴリ）: 角(5),飛(6) の SFEN 文字 → large_idx
_LARGE_PIECE_IDX = {'B':0, 'R':1}
def _pawn_bucket(n: int) -> int:
    """歩枚数 → 区分インデックス (0-6)"""
    if n <= 4: return n
    if n < 10: return 5
    return 6

# 持駒の SFEN 文字（後手は小文字）
_HAND_MAP = {
    'P':'P', 'L':'L', 'N':'N', 'S':'S', 'G':'G', 'B':'B', 'R':'R',
    'p':'P', 'l':'L', 'n':'N', 's':'S', 'g':'G', 'b':'B', 'r':'R',
}


# ── SFEN パーサー ─────────────────────────────────────────────────────────────

def _parse_board(board_str: str):
    """
    SFEN 盤面文字列を解析して {sq: (piece_char, color)} を返す。
    sq = 0-80 (段-1)*9+(列-1)、列は左(9筋)から右(1筋)。
    color: 0=先手, 1=後手
    piece_char: 'P','L',...,'+P','+L',...,'K','+K'
    """
    pieces = {}
    sq = 0
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
        promoted = False
        if c == '+':
            promoted = True
            i += 1
            c = board_str[i]
        color = 0 if c.isupper() else 1
        key = ('+' if promoted else '') + c.upper()
        pieces[sq] = (key, color)
        sq += 1
        i += 1
    return pieces


def _parse_hand(hand_str: str):
    """
    持駒文字列を解析して {(piece_char, color): count} を返す。
    piece_char: 'P','L',...（大文字）
    """
    hand = {}
    if hand_str == '-':
        return hand
    i = 0
    count = 1
    while i < len(hand_str):
        c = hand_str[i]
        if c.isdigit():
            num = 0
            while i < len(hand_str) and hand_str[i].isdigit():
                num = num * 10 + int(hand_str[i])
                i += 1
            count = num
            continue
        color = 0 if c.isupper() else 1
        pc = _HAND_MAP.get(c)
        if pc:
            hand[(pc, color)] = hand.get((pc, color), 0) + count
        count = 1
        i += 1
    return hand


def sfen_to_halfkp(sfen: str, perspective: int) -> tuple[list[int], int]:
    """
    SFEN → (アクティブ特徴インデックスリスト, バケット番号)
    perspective: 0=先手視点, 1=後手視点
    """
    parts = sfen.split()
    board_str = parts[0]
    hand_str  = parts[2] if len(parts) > 2 else '-'

    pieces = _parse_board(board_str)
    hand   = _parse_hand(hand_str)

    my_side    = perspective
    enemy_side = 1 - perspective

    # 玉の位置とタイプを取得
    my_king_sq    = my_king_lion    = None
    enemy_king_sq = enemy_king_lion = None

    for sq, (pc, color) in pieces.items():
        if pc in ('K', '+K'):
            is_lion = (pc == '+K')
            if color == my_side:
                my_king_sq   = sq
                my_king_lion = is_lion
            else:
                enemy_king_sq   = sq
                enemy_king_lion = is_lion

    if my_king_sq is None or enemy_king_sq is None:
        return [], 0

    # バケット選択
    if not my_king_lion and not enemy_king_lion:
        bucket = 0
    elif not my_king_lion:
        bucket = 1
    else:
        bucket = 2

    indices = []
    mk = my_king_sq
    ek = enemy_king_sq

    # ── 盤上の駒 ────────────────────────────────────────────────────────────
    for sq, (pc, color) in pieces.items():
        if pc in ('K', '+K'):
            continue  # 玉将・獅王は除外
        p_idx = _SFEN_PIECE_IDX.get(pc)
        if p_idx is None:
            continue
        enemy_flag = 0 if color == my_side else 1
        feat = PIECE_OFFSET + mk*(2*14*81) + enemy_flag*(14*81) + p_idx*81 + sq
        indices.append(feat)

    # ── 敵玉位置 ─────────────────────────────────────────────────────────────
    indices.append(EKING_OFFSET + mk*81 + ek)

    # ── 持駒歩: 0枚も含む7区分 one-hot（常にいずれか1つがアクティブ）─────────
    for enemy_flag in (0, 1):
        color = my_side if enemy_flag == 0 else enemy_side
        pawn_cnt = hand.get(('P', color), 0)
        bucket = _pawn_bucket(pawn_cnt)
        feat = HAND_PAWN_OFFSET + mk * 2 * HAND_PAWN_BUCKETS + enemy_flag * HAND_PAWN_BUCKETS + bucket
        indices.append(feat)

    # ── 持駒小駒・大駒 ────────────────────────────────────────────────────────
    for (pc, color), count in hand.items():
        if count <= 0:
            continue
        enemy_flag = 0 if color == my_side else 1

        if pc == 'P':  # 歩は上で処理済み
            pass

        elif pc in _SMALL_PIECE_IDX:  # 小駒
            si = _SMALL_PIECE_IDX[pc]
            cnt = min(count, 4)
            feat = HAND_SMALL_OFFSET + mk*2*4*4 + enemy_flag*4*4 + si*4 + (cnt - 1)
            indices.append(feat)

        elif pc in _LARGE_PIECE_IDX:  # 大駒
            li = _LARGE_PIECE_IDX[pc]
            cnt = min(count, 2)
            feat = HAND_LARGE_OFFSET + mk*2*2*2 + enemy_flag*2*2 + li*2 + (cnt - 1)
            indices.append(feat)

    return indices, bucket


# ── モデル ────────────────────────────────────────────────────────────────────

class NNUE_HalfKP(nn.Module):
    """
    L1 = Sparse Embedding Lookup（Dense Linear の 75倍速）
    L2 = Dense Linear (512 → L2_SIZE)
    """

    def __init__(self, feature_size=FEATURE_SIZE, l1=L1_SIZE, l2=L2_SIZE, buckets=BUCKET_COUNT):
        super().__init__()
        self.l1     = l1
        self.l2_dim = l2
        self.buckets = buckets
        # Embedding: buckets 個の (feature_size+1) × l1 テーブル（+1 = パディング行）
        self.embed = nn.ModuleList([
            nn.Embedding(feature_size + 1, l1, padding_idx=feature_size)
            for _ in range(buckets)
        ])
        self.b1  = nn.Parameter(torch.zeros(buckets, l1))
        self.fc2 = nn.Linear(l1 * 2, l2)
        self.out = nn.Linear(l2, 1)

        # 重みの初期化
        for emb in self.embed:
            nn.init.uniform_(emb.weight, -0.01, 0.01)
            emb.weight.data[feature_size] = 0  # パディング行はゼロ固定

    def _accum(self, bucket_ids: torch.Tensor, indices: torch.Tensor) -> torch.Tensor:
        """
        bucket_ids: [B]
        indices:    [B, MAX_FEATURES] (パディング = PAD_IDX)
        Returns:    [B, L1_SIZE]
        """
        # 各サンプルをバケット別に処理
        h1 = torch.zeros(indices.shape[0], self.l1, device=indices.device)
        for b in range(self.buckets):
            mask = (bucket_ids == b)
            if not mask.any():
                continue
            idx_b = indices[mask]          # [B_b, MAX_FEATURES]
            rows  = self.embed[b](idx_b)   # [B_b, MAX_FEATURES, L1]
            h1_b  = rows.sum(dim=1) + self.b1[b]
            h1[mask] = h1_b
        return h1

    def forward(self, bucket_s, idx_s, bucket_g, idx_g, side_to_move):
        """
        side_to_move: [B] 0=先手番, 1=後手番
        """
        h1s = F.relu(self._accum(bucket_s, idx_s))
        h1g = F.relu(self._accum(bucket_g, idx_g))

        # [手番側 | 手番でない側] で結合
        stm = side_to_move.bool()
        combined = torch.where(
            stm.unsqueeze(1).expand_as(torch.cat([h1g, h1s], dim=1)),
            torch.cat([h1g, h1s], dim=1),   # 後手番: [g, s]
            torch.cat([h1s, h1g], dim=1),   # 先手番: [s, g]
        )

        h2 = F.relu(self.fc2(combined))
        return self.out(h2).squeeze(-1)


# ── データロード ──────────────────────────────────────────────────────────────

def _fmt_time(seconds: float) -> str:
    s = int(seconds)
    h, rem = divmod(s, 3600)
    m, s = divmod(rem, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m}:{s:02d}"


def _bar(done: int, total: int, width: int = 28) -> str:
    ratio = min(done / total, 1.0) if total > 0 else 0.0
    filled = int(width * ratio)
    arrow = '>' if filled < width else ''
    dashes = '-' * max(0, width - filled - len(arrow))
    return f"[{'=' * filled}{arrow}{dashes}]"


def load_dataset(tsv_path: str, max_score: int = 3000):
    """TSV(SFEN\tscore) を読み込んで学習用テンソルを作る。"""
    path = Path(tsv_path)
    size_mb = path.stat().st_size / 1024 / 1024
    print(f"  読み込み中: {tsv_path}  ({size_mb:.1f} MB)")
    t0 = time.time()
    records = []
    with open(tsv_path, encoding='utf-8') as f:
        for i, line in enumerate(f):
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
            if abs(score) > max_score:
                continue
            records.append((parts[0], score / 2000.0))
            if (i + 1) % 100_000 == 0:
                print(f"\r    {i+1:,} 行読み込み済み ({len(records):,} 採用)  {_fmt_time(time.time()-t0)}", end='', flush=True)

    print(f"\r  {len(records):,} サンプル読み込み完了  ({_fmt_time(time.time()-t0)})              ")
    print(f"  特徴量変換中...")

    bucket_s_list, idx_s_list = [], []
    bucket_g_list, idx_g_list = [], []
    stm_list, target_list = [], []

    skipped = 0
    total = len(records)
    t1 = time.time()
    for i, (sfen, target) in enumerate(records):
        # side to move
        parts = sfen.split()
        stm = 0 if (len(parts) < 2 or parts[1] == 'b') else 1

        # 先手視点
        ids_s, bkt_s = sfen_to_halfkp(sfen, 0)
        # 後手視点
        ids_g, bkt_g = sfen_to_halfkp(sfen, 1)

        if (i + 1) % 50_000 == 0 or (i + 1) == total:
            pct = (i + 1) / total * 100
            eta = (time.time() - t1) / (i + 1) * (total - i - 1)
            print(f"\r    {_bar(i+1, total)}  {i+1:,}/{total:,}  {pct:3.0f}%  eta {_fmt_time(eta)}", end='', flush=True)

        if not ids_s or not ids_g:
            skipped += 1
            continue

        # パディング
        def pad(lst):
            lst = lst[:MAX_FEATURES]
            return lst + [PAD_IDX] * (MAX_FEATURES - len(lst))

        bucket_s_list.append(bkt_s)
        idx_s_list.append(pad(ids_s))
        bucket_g_list.append(bkt_g)
        idx_g_list.append(pad(ids_g))
        stm_list.append(stm)
        target_list.append(target)

    print(f"\r  特徴量変換完了  {total-skipped:,} サンプル  ({_fmt_time(time.time()-t1)})              ")
    if skipped:
        print(f"  スキップ: {skipped} サンプル（玉なし）")

    return (
        torch.tensor(bucket_s_list, dtype=torch.long),
        torch.tensor(idx_s_list,    dtype=torch.long),
        torch.tensor(bucket_g_list, dtype=torch.long),
        torch.tensor(idx_g_list,    dtype=torch.long),
        torch.tensor(stm_list,      dtype=torch.long),
        torch.tensor(target_list,   dtype=torch.float32),
    )


# ── 重みエクスポート ──────────────────────────────────────────────────────────

def export_weights_nhki(model: NNUE_HalfKP, out_path: str):
    """
    INT16/INT8量子化 NHKI バイナリ形式で出力 (CNNUE評価器HalfKPInt8 用)。

    量子化方針:
      Q1 = 127: float acc ∈ [0,1] が int16 acc ∈ [0,127] に対応。
                SSE4.1 パスが直接 clamp(acc_int16, 0, 127) → uint8 で変換するため、
                acc_int16 ≈ 127 × h1_float となるよう設計。
      Q2 = 127 / max(|W2|): W2 を int8 [-127, 127] に収める最大スケール。
                dequant_scale = 1/(127×Q2) で L2 float 結果を復元。
    """
    Q1 = 127.0
    L1_to_uint8 = 1.0  # SSE4.1 パスは scale 未使用 (直接 clip); scalar fallback 用に 1.0

    w2_np = model.fc2.weight.detach().cpu().numpy()  # [L2, L1*2]
    max_w2 = float(np.max(np.abs(w2_np)))
    max_w2 = max(max_w2, 1e-6)
    Q2 = 127.0 / max_w2

    with open(out_path, 'wb') as f:
        f.write(b'NHKI')
        f.write(struct.pack('f', Q1))
        f.write(struct.pack('f', L1_to_uint8))

        # W1_q, B1_q per bucket: int16, feature-major [FS × L1]
        for b in range(BUCKET_COUNT):
            w1 = model.embed[b].weight.detach().cpu().numpy()[:FEATURE_SIZE]  # [FS, L1]
            w1_q = np.clip(np.round(w1 * Q1), -32767, 32767).astype(np.int16)
            f.write(w1_q.flatten().tobytes())

            b1 = model.b1[b].detach().cpu().numpy()  # [L1]
            b1_q = np.clip(np.round(b1 * Q1), -32767, 32767).astype(np.int16)
            f.write(b1_q.tobytes())

        # Q2
        f.write(struct.pack('f', Q2))

        # W2_q [L2 × L1*2]: output-major (C# LoadAlignedVector256 の行アライン要件に合致)
        w2_q = np.clip(np.round(w2_np * Q2), -127, 127).astype(np.int8)
        f.write(w2_q.flatten().tobytes())  # shape [L2, L1*2] → output-major ✓

        # B2 [L2]: float32 (スケール不要、L2 accumがdequant後にfloatへ戻るため)
        f.write(model.fc2.bias.detach().cpu().numpy().astype(np.float32).tobytes())

        # W3 [L2]: float32
        f.write(model.out.weight.detach().cpu().numpy().flatten().astype(np.float32).tobytes())

        # B3: float32
        f.write(struct.pack('f', float(model.out.bias.detach().item())))

    size_kb = Path(out_path).stat().st_size // 1024
    print(f"  INT8保存: {out_path}  ({size_kb:,} KB)  Q1={Q1:.0f}  Q2={Q2:.1f}")


def export_weights(model: NNUE_HalfKP, out_path: str):
    """C# CNNUE評価器 が読み込めるバイナリ形式 (NHKP) で出力する。"""
    with open(out_path, 'wb') as f:
        f.write(b'NHKP')

        def wf(arr: np.ndarray):
            f.write(arr.astype(np.float32).tobytes())

        # W1[bucket]: [FEATURE_SIZE × L1_SIZE] (最後のパディング行を除く)
        for b in range(BUCKET_COUNT):
            w1 = model.embed[b].weight.detach().cpu().numpy()[:FEATURE_SIZE]  # [FS, L1]
            wf(w1.flatten())
            b1 = model.b1[b].detach().cpu().numpy()
            wf(b1)

        # W2: [(L1*2) × L2]、B2: [L2]
        w2 = model.fc2.weight.detach().cpu().numpy().T  # [L1*2, L2]
        wf(w2.flatten())
        wf(model.fc2.bias.detach().cpu().numpy())

        # W3: [L2]、B3: scalar
        wf(model.out.weight.detach().cpu().numpy().flatten())
        f.write(struct.pack('f', model.out.bias.detach().item()))

    size_kb = Path(out_path).stat().st_size // 1024
    print(f"  保存: {out_path}  ({size_kb:,} KB)")


# ── 学習 ─────────────────────────────────────────────────────────────────────

def load_weights(model: NNUE_HalfKP, bin_path: str) -> bool:
    """NHKP バイナリから重みをロードしてモデルに適用する（ファインチューン用）。"""
    try:
        path = Path(bin_path)
        if not path.exists():
            return False
        data = path.read_bytes()
        if data[:4] != b'NHKP':
            return False
        pos = 4
        def rf(n):
            nonlocal pos
            arr = np.frombuffer(data, dtype=np.float32, count=n, offset=pos).copy()
            pos += n * 4
            return arr

        for b in range(BUCKET_COUNT):
            w1 = rf(FEATURE_SIZE * L1_SIZE).reshape(FEATURE_SIZE, L1_SIZE)
            b1 = rf(L1_SIZE)
            model.embed[b].weight.data[:FEATURE_SIZE] = torch.from_numpy(w1)
            model.b1.data[b] = torch.from_numpy(b1)

        w2 = rf(L1_SIZE * 2 * L2_SIZE).reshape(L1_SIZE * 2, L2_SIZE)
        b2 = rf(L2_SIZE)
        model.fc2.weight.data = torch.from_numpy(w2.T)
        model.fc2.bias.data   = torch.from_numpy(b2)

        w3 = rf(L2_SIZE)
        b3 = np.frombuffer(data, dtype=np.float32, count=1, offset=pos)[0]
        model.out.weight.data = torch.from_numpy(w3).unsqueeze(0)
        model.out.bias.data   = torch.tensor([b3])
        return True
    except Exception as e:
        print(f"  警告: 重みのロードに失敗 ({e})")
        return False


def train(data_path: str, out_path: str, epochs: int = 20, batch: int = 4096,
          lr: float = 1e-3, resume: str | None = None,
          export_int8: str | None = None):
    device = torch.device('cpu')  # GTX 1060 (sm_61) CUDA 非対応
    print(f"device={device}")

    bkt_s, idx_s, bkt_g, idx_g, stm, y = load_dataset(data_path)
    n = len(y)
    perm  = torch.randperm(n)
    split = int(n * 0.9)
    tr = perm[:split]
    va = perm[split:]

    def to_dev(*ts): return tuple(t.to(device) for t in ts)

    model    = NNUE_HalfKP().to(device)
    if resume:
        ok = load_weights(model, resume)
        print(f"  ファインチューン: {resume}  ({'OK' if ok else '新規学習にフォールバック'})")
    opt      = torch.optim.Adam(model.parameters(), lr=lr, weight_decay=1e-5)
    sched    = torch.optim.lr_scheduler.CosineAnnealingLR(opt, T_max=epochs)
    loss_fn  = nn.MSELoss()

    n_batches = math.ceil(len(tr) / batch)
    print(f"\n  学習開始  train {len(tr):,}  val {len(va):,}  "
          f"{n_batches} バッチ/epoch  epochs={epochs}", flush=True)
    print(f"  {'─'*70}", flush=True)

    t_total = time.time()
    best_val = float('inf')
    best_ep  = 0

    for ep in range(1, epochs + 1):
        model.train()
        perm_tr = tr[torch.randperm(len(tr))]
        tr_loss = 0.0
        t_ep = time.time()

        for bi_idx, start in enumerate(range(0, len(perm_tr), batch)):
            bi = perm_tr[start:start+batch]
            bs, is_, bg, ig, sm, yt = to_dev(
                bkt_s[bi], idx_s[bi], bkt_g[bi], idx_g[bi], stm[bi], y[bi])
            pred = model(bs, is_, bg, ig, sm)
            loss = loss_fn(pred, yt)
            opt.zero_grad()
            loss.backward()
            opt.step()
            tr_loss += loss.item() * len(bi)

            done = bi_idx + 1
            elapsed = time.time() - t_ep
            eta = elapsed / done * (n_batches - done) if done > 0 else 0
            cur_loss = tr_loss / (done * batch)
            print(
                f"\r  Epoch {ep:2d}/{epochs}  {_bar(done, n_batches)}  "
                f"{done}/{n_batches}  {done/n_batches*100:3.0f}%  "
                f"eta {_fmt_time(eta)}  loss {cur_loss:.5f}",
                end='', flush=True)

        tr_loss /= len(tr)

        model.eval()
        with torch.no_grad():
            bs, is_, bg, ig, sm, yv = to_dev(
                bkt_s[va], idx_s[va], bkt_g[va], idx_g[va], stm[va], y[va])
            va_loss = loss_fn(model(bs, is_, bg, ig, sm), yv).item()

        sched.step()
        ep_time = time.time() - t_ep
        is_best = va_loss < best_val
        best_mark = '  ★最良' if is_best else ''

        print(
            f"\r  Epoch {ep:2d}/{epochs}  {_bar(n_batches, n_batches)}  "
            f"{n_batches}/{n_batches}  100%  "
            f"{_fmt_time(ep_time)}  train {tr_loss:.5f}  val {va_loss:.5f}{best_mark}",
            flush=True)

        if is_best:
            best_val = va_loss
            best_ep  = ep
            export_weights(model, out_path)
            if export_int8:
                export_weights_nhki(model, export_int8)

    elapsed_total = time.time() - t_total
    print(f"\n  {'─'*70}")
    print(f"  完了  合計 {_fmt_time(elapsed_total)}  最良 val={best_val:.5f} (epoch {best_ep})")
    print(f"  出力: {out_path}")


# ── エントリポイント ──────────────────────────────────────────────────────────

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description='HalfKP NNUE 学習')
    parser.add_argument('--data',   required=True)
    parser.add_argument('--out',    required=True)
    parser.add_argument('--epochs', type=int,   default=20)
    parser.add_argument('--batch',  type=int,   default=4096)
    parser.add_argument('--lr',     type=float, default=1e-3)
    parser.add_argument('--resume',     default=None, help='前世代の重みファイル (ファインチューン用)')
    parser.add_argument('--export-int8', default=None, dest='export_int8',
                        help='INT8量子化 NHKI ファイルの出力先 (CNNUE評価器HalfKPInt8 用)')
    args = parser.parse_args()

    train(args.data, args.out, args.epochs, args.batch, args.lr,
          args.resume, args.export_int8)
