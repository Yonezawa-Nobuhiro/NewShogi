using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>
/// INT8 量子化 NNUE 評価器。
/// Avx2 の MultiplyAddAdjacent (pmaddubsw) を使い L2 を高速化。
///
/// 重みファイル形式 (nnue_weights_int8.bin):
///   Magic  : "NNI8" (4 bytes)
///   scale  : float32 × 3  (s1, s2, s3)
///   W1_q   : FEATURE_SIZE × L1_SIZE  int8  feature-major
///   B1     : L1_SIZE  float32
///   W2_q   : L2_SIZE × L1_SIZE       int8  output-major
///   B2     : L2_SIZE  float32
///   W3_q   : L2_SIZE  int8
///   B3     : float32
/// </summary>
public sealed class CNNUE評価器Int8
{
    // ── 定数 ────────────────────────────────────────────────────────────────
    private const int FEATURE_SIZE = 2_606;
    private const int L1_SIZE      = 256;
    private const int L2_SIZE      = 64;
    private const int BOARD_BASE   = 0;
    private const int HAND_BASE    = 2 * 16 * 81;

    private static readonly E駒種[] HandPieces =
    [
        E駒種.歩兵, E駒種.香車, E駒種.桂馬,
        E駒種.銀将, E駒種.金将, E駒種.角行, E駒種.飛車
    ];

    // ── フィールド ──────────────────────────────────────────────────────────
    private readonly sbyte[] _w1q;   // [FEATURE_SIZE × L1_SIZE]  int8 feat-major
    private readonly float[] _b1;    // [L1_SIZE]
    private readonly sbyte[] _w2q;   // [L2_SIZE × L1_SIZE]       int8 output-major
    private readonly float[] _b2;    // [L2_SIZE]
    private readonly sbyte[] _w3q;   // [L2_SIZE]                 int8
    private readonly float   _b3;
    private readonly float   _inv_s1; // 1 / scale_w1
    private readonly float   _inv_s2; // 1 / scale_w2
    private readonly float   _inv_s3; // 1 / scale_w3

    private CNNUE評価器Int8(
        sbyte[] w1q, float[] b1, float inv_s1,
        sbyte[] w2q, float[] b2, float inv_s2,
        sbyte[] w3q, float b3, float inv_s3)
    {
        _w1q = w1q; _b1 = b1; _inv_s1 = inv_s1;
        _w2q = w2q; _b2 = b2; _inv_s2 = inv_s2;
        _w3q = w3q; _b3 = b3; _inv_s3 = inv_s3;
    }

    // ── ロード ──────────────────────────────────────────────────────────────

    public static CNNUE評価器Int8? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            var magic = br.ReadBytes(4);
            if (magic[0] != 'N' || magic[1] != 'N' || magic[2] != 'I' || magic[3] != '8')
                return null;

            float s1 = br.ReadSingle(), s2 = br.ReadSingle(), s3 = br.ReadSingle();

            sbyte[] ReadInt8(int n)
            {
                var buf = br.ReadBytes(n);
                return MemoryMarshal.Cast<byte, sbyte>(buf).ToArray();
            }
            float[] ReadFloats(int n)
            {
                var buf = br.ReadBytes(n * 4);
                var arr = new float[n];
                Buffer.BlockCopy(buf, 0, arr, 0, buf.Length);
                return arr;
            }

            var w1q = ReadInt8(FEATURE_SIZE * L1_SIZE);
            var b1  = ReadFloats(L1_SIZE);
            var w2q = ReadInt8(L2_SIZE * L1_SIZE);
            var b2  = ReadFloats(L2_SIZE);
            var w3q = ReadInt8(L2_SIZE);
            var b3  = br.ReadSingle();

            return new CNNUE評価器Int8(w1q, b1, 1f / s1, w2q, b2, 1f / s2, w3q, b3, 1f / s3);
        }
        catch { return null; }
    }

    // ── 評価 ────────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Evaluate(C盤面 盤面)
    {
        // ── L1: スパース INT8 → float アキュム ─────────────────────────────
        Span<float> h1 = stackalloc float[L1_SIZE];
        _b1.CopyTo(h1);

        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 盤面.Get駒(列, 段);
            if (駒 == null) continue;
            int sq = (段 - 1) * 9 + (列 - 1);
            int player = 駒.手番 == E手番.先手 ? 0 : 1;
            int piece = (int)駒.種類 - 1;
            if (piece < 0) continue;
            AddInt8Row(h1, (BOARD_BASE + player * 16 * 81 + piece * 81 + sq) * L1_SIZE, 1f);
        }

        for (int p = 0; p < 2; p++)
        {
            var 持ち駒 = p == 0 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;
            for (int hi = 0; hi < HandPieces.Length; hi++)
            {
                持ち駒.TryGetValue(HandPieces[hi], out int 枚数);
                if (枚数 <= 0) continue;
                AddInt8Row(h1, (HAND_BASE + p * 7 + hi) * L1_SIZE, 枚数 / 10f);
            }
        }

        // dequant + ReLU → uint8 (0..127)
        Span<byte> h1u = stackalloc byte[L1_SIZE];
        for (int i = 0; i < L1_SIZE; i++)
        {
            float v = h1[i] * _inv_s1;
            h1u[i] = v <= 0f ? (byte)0 : (v >= 127f ? (byte)127 : (byte)(int)v);
        }

        return EvalL2(h1u);
    }

    // ── L2 以降（Avx2 最適化パス / フォールバック） ─────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int EvalL2(Span<byte> h1u)
    {
        return Avx2.IsSupported ? EvalL2Avx2(h1u) : EvalL2Scalar(h1u);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe int EvalL2Avx2(Span<byte> h1u)
    {
        // W2_q は [L1_SIZE, L2_SIZE] = [256, 64] の input-major レイアウト（AXPY用）。
        // 各入力 i (h1u[i]≠0) について W2[i, 0..63] を h2_acc に加算。
        // 水平集約なし → AVX/SSE 遷移ペナルティ回避。
        Span<int> h2_acc = stackalloc int[L2_SIZE];

        fixed (byte*  pH1 = h1u)
        fixed (sbyte* pW2 = _w2q)
        fixed (int*   pAcc = h2_acc)
        {
            for (int i = 0; i < L1_SIZE; i++)
            {
                int s = pH1[i];
                if (s == 0) continue;

                var vs = Vector256.Create(s);          // [s,s,s,s,s,s,s,s] int32
                sbyte* pRow = pW2 + i * L2_SIZE;

                // 64 outputs を 8 要素ずつ処理（8 iterations）
                for (int j = 0; j < L2_SIZE; j += 8)
                {
                    // 8 int8 → 8 int32
                    long raw = Unsafe.ReadUnaligned<long>(pRow + j);
                    var w128 = Vector128.Create(raw, 0L).AsSByte();
                    var w_i32 = Avx2.ConvertToVector256Int32(w128);
                    // acc += s × W2[i, j..j+7]  (pmulld + paddd)
                    var acc = Avx.LoadVector256(pAcc + j);
                    Avx.Store(pAcc + j, Avx2.Add(acc, Avx2.MultiplyLow(vs, w_i32)));
                }
            }
        }

        // int32 アキュム → dequant → bias → ReLU → float h2
        Span<float> h2 = stackalloc float[L2_SIZE];
        float dq = _inv_s1 * _inv_s2;
        for (int j = 0; j < L2_SIZE; j++)
        {
            float v = h2_acc[j] * dq + _b2[j];
            h2[j] = v > 0f ? v : 0f;
        }

        return EvalOutput(h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int EvalL2Scalar(Span<byte> h1u)
    {
        Span<int> h2_acc = stackalloc int[L2_SIZE];
        for (int i = 0; i < L1_SIZE; i++)
        {
            int s = h1u[i];
            if (s == 0) continue;
            int off = i * L2_SIZE;
            for (int j = 0; j < L2_SIZE; j++)
                h2_acc[j] += s * _w2q[off + j];
        }
        Span<float> h2 = stackalloc float[L2_SIZE];
        float dq = _inv_s1 * _inv_s2;
        for (int j = 0; j < L2_SIZE; j++)
        {
            float v = h2_acc[j] * dq + _b2[j];
            h2[j] = v > 0f ? v : 0f;
        }
        return EvalOutput(h2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int EvalOutput(Span<float> h2)
    {
        float score = _b3;
        for (int i = 0; i < L2_SIZE; i++)
            score += h2[i] * _w3q[i] * _inv_s3;
        return (int)(score * 2000f);
    }

    // ── ヘルパー ────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddInt8Row(Span<float> h1, int offset, float scale)
    {
        if (Avx2.IsSupported)
        {
            AddInt8RowAvx2(h1, offset, scale);
            return;
        }
        for (int j = 0; j < L1_SIZE; j++)
            h1[j] += _w1q[offset + j] * scale;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void AddInt8RowAvx2(Span<float> h1, int offset, float scale)
    {
        // int8 × 8 → int32 → float → FMA（Avx2 を使い8要素ずつ処理）
        var vscale = Vector256.Create(scale);
        fixed (float* pH1 = h1)
        fixed (sbyte* pW = _w1q)
        {
            sbyte* pRow = pW + offset;
            for (int j = 0; j < L1_SIZE; j += 8)
            {
                // 8 bytes int8 → lower 64bit of Vector128 → sign extend → 8 int32 → float
                long raw = Unsafe.ReadUnaligned<long>(pRow + j);
                var w128 = Vector128.Create(raw, 0L).AsSByte();
                var w_i32 = Avx2.ConvertToVector256Int32(w128);
                var w_f32 = Avx.ConvertToVector256Single(w_i32);
                var h1v   = Avx.LoadVector256(pH1 + j);
                Avx.Store(pH1 + j, Avx.Add(h1v, Avx.Multiply(w_f32, vscale)));
            }
        }
    }

}
