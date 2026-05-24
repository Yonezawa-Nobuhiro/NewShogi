using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace 変成将棋.NNUE;

/// <summary>
/// INT8 中間層（L2）。uint8[L1×2] → float[L2]。
///
/// AVX2 path:
///   pmaddubsw (uint8 × int8 → int16ペア和) + pmaddwd (int16ペア → int32) を
///   4出力ニューロン同時・32入力ずつ処理。
///   float版 (8要素/op) に対し 4倍幅の要素を処理できる。
///
/// 荷重レイアウト: W2[L2数][L1×2] (出力優先・行=512バイト・32バイトアライン)
/// </summary>
internal sealed unsafe class C中間層Int8 : IDisposable
{
    private const int L1x2 = CNNUE評価器HalfKPInt8.L1数 * 2;  // 512
    private const int L2   = CNNUE評価器HalfKPInt8.L2数;       // 64

    private readonly nint _w2;     // sbyte*(L2 × L1x2), 32バイトアライン
    private readonly float[] _b2;  // float[L2]
    internal readonly float dequant_scale;  // 1/(127×Q2): int32アキュム→float

    private bool _disposed;

    internal C中間層Int8(sbyte[] w2, float[] b2, float dequantScale)
    {
        uint sz = (uint)(L2 * L1x2);
        var pw = (sbyte*)NativeMemory.AlignedAlloc(sz, 32);
        fixed (sbyte* src = w2) Unsafe.CopyBlockUnaligned((void*)pw, (void*)src, sz);
        _w2 = (nint)pw;
        _b2 = b2;
        dequant_scale = dequantScale;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_w2 != 0) NativeMemory.AlignedFree((void*)_w2);
    }

    // ── Forward ──────────────────────────────────────────────────────────────

    /// <summary>uint8[512] → float[L2](ReLU済み)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Forward(byte* h1u8, Span<float> output)
    {
        sbyte* W = (sbyte*)_w2;
        float dq = dequant_scale;

        fixed (float* pOut = output)
        fixed (float* pB2  = _b2)
        {
            if (Avx2.IsSupported && Ssse3.IsSupported)
                ForwardAvx2(h1u8, W, pOut, pB2, dq);
            else
                ForwardScalar(h1u8, W, pOut, pB2, dq);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ForwardAvx2(byte* h1u8, sbyte* W, float* output, float* b2, float dq)
    {
        // ones: int16[16] = {1,1,...} — pmaddwd で int16ペア → int32 に使う
        var ones = Vector256.Create((short)1);

        // 8出力ニューロンを同時処理（8グループ × 8 = 64出力）
        // HSum 呼び出し回数を 64 → 32 に半減
        for (int g = 0; g < L2; g += 8)
        {
            var acc0 = Vector256<int>.Zero;
            var acc1 = Vector256<int>.Zero;
            var acc2 = Vector256<int>.Zero;
            var acc3 = Vector256<int>.Zero;
            var acc4 = Vector256<int>.Zero;
            var acc5 = Vector256<int>.Zero;
            var acc6 = Vector256<int>.Zero;
            var acc7 = Vector256<int>.Zero;

            sbyte* w0 = W + (g + 0) * L1x2;
            sbyte* w1 = W + (g + 1) * L1x2;
            sbyte* w2 = W + (g + 2) * L1x2;
            sbyte* w3 = W + (g + 3) * L1x2;
            sbyte* w4 = W + (g + 4) * L1x2;
            sbyte* w5 = W + (g + 5) * L1x2;
            sbyte* w6 = W + (g + 6) * L1x2;
            sbyte* w7 = W + (g + 7) * L1x2;

            for (int i = 0; i < L1x2; i += 32)
            {
                var a = Avx.LoadVector256(h1u8 + i);
                acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w0 + i)), ones));
                acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w1 + i)), ones));
                acc2 = Avx2.Add(acc2, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w2 + i)), ones));
                acc3 = Avx2.Add(acc3, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w3 + i)), ones));
                acc4 = Avx2.Add(acc4, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w4 + i)), ones));
                acc5 = Avx2.Add(acc5, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w5 + i)), ones));
                acc6 = Avx2.Add(acc6, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w6 + i)), ones));
                acc7 = Avx2.Add(acc7, Avx2.MultiplyAddAdjacent(
                    Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w7 + i)), ones));
            }

            output[g + 0] = MathF.Max(0f, HSum(acc0) * dq + b2[g + 0]);
            output[g + 1] = MathF.Max(0f, HSum(acc1) * dq + b2[g + 1]);
            output[g + 2] = MathF.Max(0f, HSum(acc2) * dq + b2[g + 2]);
            output[g + 3] = MathF.Max(0f, HSum(acc3) * dq + b2[g + 3]);
            output[g + 4] = MathF.Max(0f, HSum(acc4) * dq + b2[g + 4]);
            output[g + 5] = MathF.Max(0f, HSum(acc5) * dq + b2[g + 5]);
            output[g + 6] = MathF.Max(0f, HSum(acc6) * dq + b2[g + 6]);
            output[g + 7] = MathF.Max(0f, HSum(acc7) * dq + b2[g + 7]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ForwardScalar(byte* h1u8, sbyte* W, float* output, float* b2, float dq)
    {
        Span<int> acc = stackalloc int[L2];
        acc.Clear();
        for (int i = 0; i < L1x2; i++)
        {
            int v = h1u8[i];
            if (v == 0) continue;
            int off = i;
            sbyte* row = W + off;  // W は [L2][L1x2] なので列アクセス → ストライド
            // スカラーフォールバックは row-major [L1x2][L2] レイアウトを想定
            // ※ロード時に転置しておく必要がある（または別途対応）
            for (int j = 0; j < L2; j++)
                acc[j] += v * W[j * L1x2 + i];
        }
        for (int j = 0; j < L2; j++)
            output[j] = MathF.Max(0f, acc[j] * dq + b2[j]);
    }

    // ── Vector256<int> 水平和 ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HSum(Vector256<int> v)
    {
        // 上半分と下半分を加算 → 4×int32
        var v128 = Sse2.Add(v.GetLower(), v.GetUpper());
        // phaddd より shuffle+add の方がスループット有利
        var shuf = Sse2.Shuffle(v128, 0b_10_11_00_01);  // [1,0,3,2]
        v128 = Sse2.Add(v128, shuf);                     // [0+1, 0+1, 2+3, 2+3]
        shuf = Sse2.Shuffle(v128, 0b_00_00_10_10);       // [2+3, 2+3, 0+1, 0+1]
        v128 = Sse2.Add(v128, shuf);
        return v128.GetElement(0);
    }
}
