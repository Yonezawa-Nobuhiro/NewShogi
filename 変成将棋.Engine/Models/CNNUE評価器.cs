using System.Numerics;
using System.Runtime.CompilerServices;

namespace 変成将棋.Models;

/// <summary>
/// NNUE 評価器（SIMD + インクリメンタル更新対応）。
///
/// 特徴量:
///   Board: 先手/後手 × 16駒種 × 81升 = 2,592 スパース binary
///   Hand : 先手/後手 × 7種 = 14 連続 (枚数/10)
///   計 2,606 次元
///
/// ネットワーク: 2606 → 256 (ReLU) → 64 (ReLU) → 1
/// </summary>
public sealed class CNNUE評価器
{
    public const int FEATURE_SIZE = 2_606;
    public const int L1_SIZE      = 256;
    public const int L2_SIZE      = 64;

    private const int BOARD_BASE = 0;
    private const int HAND_BASE  = 2 * 16 * 81;  // 2592

    internal static readonly E駒種[] HandPieces =
    [
        E駒種.歩兵, E駒種.香車, E駒種.桂馬,
        E駒種.銀将, E駒種.金将, E駒種.角行, E駒種.飛車
    ];

    private readonly float[] _w1;  // [FEATURE_SIZE * L1_SIZE]  feat-major
    private readonly float[] _b1;  // [L1_SIZE]
    private readonly float[] _w2;  // [L1_SIZE * L2_SIZE]       L1-major
    private readonly float[] _b2;  // [L2_SIZE]
    private readonly float[] _w3;  // [L2_SIZE]
    private readonly float   _b3;

    private static readonly int _vecCount = Vector<float>.Count;

    private CNNUE評価器(float[] w1, float[] b1, float[] w2, float[] b2, float[] w3, float b3)
    {
        _w1 = w1; _b1 = b1; _w2 = w2; _b2 = b2; _w3 = w3; _b3 = b3;
    }

    public static CNNUE評価器? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            float[] ReadFloats(int n)
            {
                var arr   = new float[n];
                var bytes = br.ReadBytes(n * 4);
                Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
                return arr;
            }

            var w1 = ReadFloats(FEATURE_SIZE * L1_SIZE);
            var b1 = ReadFloats(L1_SIZE);
            var w2 = ReadFloats(L1_SIZE * L2_SIZE);
            var b2 = ReadFloats(L2_SIZE);
            var w3 = ReadFloats(L2_SIZE);
            var b3 = br.ReadSingle();
            return new CNNUE評価器(w1, b1, w2, b2, w3, b3);
        }
        catch { return null; }
    }

    // ── L1 アキュムレータ管理 ──────────────────────────────────────────────

    /// <summary>盤面から L1 アキュムレータをスクラッチ計算する。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ComputeAccum(C盤面 盤面, float[] accum)
    {
        _b1.CopyTo(accum, 0);

        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 盤面.Get駒(列, 段);
            if (駒 == null) continue;
            int sq     = (段 - 1) * 9 + (列 - 1);
            int player = 駒.手番 == E手番.先手 ? 0 : 1;
            int piece  = (int)駒.種類 - 1;
            if (piece < 0) continue;
            AddWeightRow(accum, (BOARD_BASE + player * 16 * 81 + piece * 81 + sq) * L1_SIZE, 1f);
        }

        for (int p = 0; p < 2; p++)
        {
            var 持ち駒 = p == 0 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;
            for (int hi = 0; hi < HandPieces.Length; hi++)
            {
                持ち駒.TryGetValue(HandPieces[hi], out int 枚数);
                if (枚数 <= 0) continue;
                AddWeightRow(accum, (HAND_BASE + p * 7 + hi) * L1_SIZE, 枚数 / 10f);
            }
        }
    }

    /// <summary>
    /// 1手分の差分で L1 アキュムレータをインクリメンタル更新する。
    /// Apply 後の盤面情報（取消情報 + 移動後の駒）を渡す。
    /// Apply 前の盤面を参照しないため、合法手確定後に呼ぶこと。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void UpdateAccumAfterApply(
        float[]    accum,
        S手        手,
        E手番      指した側,       // Apply 前の手番
        E駒種?    movedKind,      // 移動前の駒種（打ち以外。Apply後に Get駒(dst) で取得）
        C駒?      dstCapture,    // 取消情報.取り駒
        C駒?      midCapture)    // 取消情報.中間取り駒
    {
        int player   = 指した側 == E手番.先手 ? 0 : 1;
        int opponent = 1 - player;

        if (手.Is打ち)
        {
            var 駒種 = 手.Get打ち駒;
            var 先  = 手.Get移動先;
            int dstSq = (先.段 - 1) * 9 + (先.列 - 1);

            AddWeightRow(accum, (BOARD_BASE + player * 16 * 81 + ((int)駒種 - 1) * 81 + dstSq) * L1_SIZE, 1f);

            int handIdx = IndexOfHandPiece(駒種);
            if (handIdx >= 0)
                AddWeightRow(accum, (HAND_BASE + player * 7 + handIdx) * L1_SIZE, -1f / 10f);
        }
        else if (movedKind != null && movedKind != E駒種.なし)
        {
            var src   = 手.Get移動元;
            var dst   = 手.Get移動先;
            int srcSq = (src.段 - 1) * 9 + (src.列 - 1);
            int dstSq = (dst.段 - 1) * 9 + (dst.列 - 1);

            // 移動元の駒（元の駒種）を除去
            AddWeightRow(accum, (BOARD_BASE + player * 16 * 81 + ((int)movedKind - 1) * 81 + srcSq) * L1_SIZE, -1f);

            // 移動先に現れる駒（成り後の駒種）
            var newKind = 手.Is成り ? C駒.Get成り後(movedKind.Value) : movedKind.Value;
            AddWeightRow(accum, (BOARD_BASE + player * 16 * 81 + ((int)newKind - 1) * 81 + dstSq) * L1_SIZE, 1f);

            // 獅王 中間取り
            if (midCapture != null)
            {
                var mid   = 手.Get中間;
                int midSq = (mid.段 - 1) * 9 + (mid.列 - 1);
                AddWeightRow(accum, (BOARD_BASE + opponent * 16 * 81 + ((int)midCapture.種類 - 1) * 81 + midSq) * L1_SIZE, -1f);
                int handIdx = IndexOfHandPiece(C駒.Get成り前(midCapture.種類));
                if (handIdx >= 0)
                    AddWeightRow(accum, (HAND_BASE + player * 7 + handIdx) * L1_SIZE, 1f / 10f);
            }

            // 移動先取り
            if (dstCapture != null)
            {
                AddWeightRow(accum, (BOARD_BASE + opponent * 16 * 81 + ((int)dstCapture.種類 - 1) * 81 + dstSq) * L1_SIZE, -1f);
                int handIdx = IndexOfHandPiece(C駒.Get成り前(dstCapture.種類));
                if (handIdx >= 0)
                    AddWeightRow(accum, (HAND_BASE + player * 7 + handIdx) * L1_SIZE, 1f / 10f);
            }
        }
    }

    /// <summary>L1 アキュムレータから評価値を計算する（完全インライン・ヒープ確保なし）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int EvaluateFromAccum(float[] accum)
    {
        int vc = _vecCount;
        // accum を stack にコピーして ReLU
        Span<float> h1 = stackalloc float[L1_SIZE];
        accum.CopyTo(h1);
        var zero = Vector<float>.Zero;
        int i = 0;
        for (; i <= L1_SIZE - vc; i += vc)
        {
            var v = new Vector<float>(h1.Slice(i));
            Vector.Max(v, zero).CopyTo(h1.Slice(i));
        }
        for (; i < L1_SIZE; i++) if (h1[i] < 0) h1[i] = 0;

        // L2（インライン）
        Span<float> h2 = stackalloc float[L2_SIZE];
        _b2.CopyTo(h2);
        for (int ii = 0; ii < L1_SIZE; ii++)
        {
            float v1 = h1[ii];
            if (v1 == 0f) continue;
            var vv1 = new Vector<float>(v1);
            int off = ii * L2_SIZE;
            int j = 0;
            for (; j <= L2_SIZE - vc; j += vc)
                (new Vector<float>(h2.Slice(j)) + vv1 * new Vector<float>(_w2, off + j))
                    .CopyTo(h2.Slice(j));
            for (; j < L2_SIZE; j++) h2[j] += v1 * _w2[off + j];
        }
        i = 0;
        for (; i <= L2_SIZE - vc; i += vc)
        {
            var v = new Vector<float>(h2.Slice(i));
            Vector.Max(v, zero).CopyTo(h2.Slice(i));
        }
        for (; i < L2_SIZE; i++) if (h2[i] < 0) h2[i] = 0;

        // Output
        float score = _b3;
        var vScore = Vector<float>.Zero;
        i = 0;
        for (; i <= L2_SIZE - vc; i += vc)
            vScore += new Vector<float>(h2.Slice(i)) * new Vector<float>(_w3, i);
        for (int k = 0; k < vc; k++) score += vScore[k];
        for (; i < L2_SIZE; i++) score += h2[i] * _w3[i];
        return (int)(score * 2000f);
    }

    /// <summary>スクラッチから評価値を計算する（完全インライン・ヒープ確保なし）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Evaluate(C盤面 盤面)
    {
        int vc = _vecCount;
        Span<float> h1 = stackalloc float[L1_SIZE];
        _b1.CopyTo(h1);

        // L1: 盤面（array-index Vector で JIT 最適化を最大化）
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 盤面.Get駒(列, 段);
            if (駒 == null) continue;
            int sq     = (段 - 1) * 9 + (列 - 1);
            int player = 駒.手番 == E手番.先手 ? 0 : 1;
            int piece  = (int)駒.種類 - 1;
            if (piece < 0) continue;
            int off = (BOARD_BASE + player * 16 * 81 + piece * 81 + sq) * L1_SIZE;
            int j = 0;
            for (; j <= L1_SIZE - vc; j += vc)
                (new Vector<float>(h1.Slice(j)) + new Vector<float>(_w1, off + j))
                    .CopyTo(h1.Slice(j));
            for (; j < L1_SIZE; j++) h1[j] += _w1[off + j];
        }

        // L1: 持ち駒
        for (int p = 0; p < 2; p++)
        {
            var 持ち駒 = p == 0 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;
            for (int hi = 0; hi < HandPieces.Length; hi++)
            {
                持ち駒.TryGetValue(HandPieces[hi], out int 枚数);
                if (枚数 <= 0) continue;
                float scale = 枚数 / 10f;
                int off = (HAND_BASE + p * 7 + hi) * L1_SIZE;
                var vs = new Vector<float>(scale);
                int j = 0;
                for (; j <= L1_SIZE - vc; j += vc)
                    (new Vector<float>(h1.Slice(j)) + new Vector<float>(_w1, off + j) * vs)
                        .CopyTo(h1.Slice(j));
                for (; j < L1_SIZE; j++) h1[j] += _w1[off + j] * scale;
            }
        }

        // ReLU
        var zero = Vector<float>.Zero;
        int i = 0;
        for (; i <= L1_SIZE - vc; i += vc)
        {
            var v = new Vector<float>(h1.Slice(i));
            Vector.Max(v, zero).CopyTo(h1.Slice(i));
        }
        for (; i < L1_SIZE; i++) if (h1[i] < 0) h1[i] = 0;

        // L2
        Span<float> h2 = stackalloc float[L2_SIZE];
        _b2.CopyTo(h2);
        for (int ii = 0; ii < L1_SIZE; ii++)
        {
            float v1 = h1[ii];
            if (v1 == 0f) continue;
            var vv1 = new Vector<float>(v1);
            int off = ii * L2_SIZE;
            int j = 0;
            for (; j <= L2_SIZE - vc; j += vc)
                (new Vector<float>(h2.Slice(j)) + vv1 * new Vector<float>(_w2, off + j))
                    .CopyTo(h2.Slice(j));
            for (; j < L2_SIZE; j++) h2[j] += v1 * _w2[off + j];
        }
        i = 0;
        for (; i <= L2_SIZE - vc; i += vc)
        {
            var v = new Vector<float>(h2.Slice(i));
            Vector.Max(v, zero).CopyTo(h2.Slice(i));
        }
        for (; i < L2_SIZE; i++) if (h2[i] < 0) h2[i] = 0;

        // Output
        float score = _b3;
        var vScore = Vector<float>.Zero;
        i = 0;
        for (; i <= L2_SIZE - vc; i += vc)
            vScore += new Vector<float>(h2.Slice(i)) * new Vector<float>(_w3, i);
        for (int k = 0; k < vc; k++) score += vScore[k];
        for (; i < L2_SIZE; i++) score += h2[i] * _w3[i];

        return (int)(score * 2000f);
    }

    // ── 内部ヘルパー ───────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int ComputeL2Output(Span<float> h1)
    {
        // L2
        Span<float> h2 = stackalloc float[L2_SIZE];
        _b2.CopyTo(h2);

        for (int ii = 0; ii < L1_SIZE; ii++)
        {
            float v1 = h1[ii];
            if (v1 == 0f) continue;
            int offset = ii * L2_SIZE;
            var vv1 = new Vector<float>(v1);
            int j = 0;
            for (; j <= L2_SIZE - _vecCount; j += _vecCount)
            {
                var w   = new Vector<float>(_w2, offset + j);
                var acc = new Vector<float>(h2.Slice(j));
                (acc + vv1 * w).CopyTo(h2.Slice(j));
            }
            for (; j < L2_SIZE; j++)
                h2[j] += v1 * _w2[offset + j];
        }

        var zero = Vector<float>.Zero;
        int i = 0;
        for (; i <= L2_SIZE - _vecCount; i += _vecCount)
        {
            var v = new Vector<float>(h2.Slice(i));
            Vector.Max(v, zero).CopyTo(h2.Slice(i));
        }
        for (; i < L2_SIZE; i++)
            if (h2[i] < 0) h2[i] = 0;

        // Output
        float score = _b3;
        var vScore = Vector<float>.Zero;
        i = 0;
        for (; i <= L2_SIZE - _vecCount; i += _vecCount)
            vScore += new Vector<float>(h2.Slice(i)) * new Vector<float>(_w3, i);
        for (int k = 0; k < _vecCount; k++)
            score += vScore[k];
        for (; i < L2_SIZE; i++)
            score += h2[i] * _w3[i];

        return (int)(score * 2000f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddWeightRow(Span<float> accum, int offset, float scale)
    {
        if (scale == 1f)
        {
            int j = 0;
            for (; j <= L1_SIZE - _vecCount; j += _vecCount)
                (new Vector<float>(accum.Slice(j)) + new Vector<float>(_w1, offset + j))
                    .CopyTo(accum.Slice(j));
            for (; j < L1_SIZE; j++)
                accum[j] += _w1[offset + j];
        }
        else if (scale == -1f)
        {
            int j = 0;
            for (; j <= L1_SIZE - _vecCount; j += _vecCount)
                (new Vector<float>(accum.Slice(j)) - new Vector<float>(_w1, offset + j))
                    .CopyTo(accum.Slice(j));
            for (; j < L1_SIZE; j++)
                accum[j] -= _w1[offset + j];
        }
        else
        {
            var vs = new Vector<float>(scale);
            int j  = 0;
            for (; j <= L1_SIZE - _vecCount; j += _vecCount)
                (new Vector<float>(accum.Slice(j)) + new Vector<float>(_w1, offset + j) * vs)
                    .CopyTo(accum.Slice(j));
            for (; j < L1_SIZE; j++)
                accum[j] += _w1[offset + j] * scale;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int IndexOfHandPiece(E駒種 種類)
    {
        for (int i = 0; i < HandPieces.Length; i++)
            if (HandPieces[i] == 種類) return i;
        return -1;
    }
}
