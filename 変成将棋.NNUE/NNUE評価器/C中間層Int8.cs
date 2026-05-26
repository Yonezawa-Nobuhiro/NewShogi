using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace 変成将棋.NNUE;

/// <summary>
/// INT8 中間層（L2）。uint8[L1×2] → short[L2]（ReLU済み・signed saturate）。
///
/// AVX2 path:
///   pmaddubsw + pmaddwd で int32 アキュム → バイアス加算(short→int sign-extend) →
///   max(0,x) → packssdw で 2×int32[8] を short[16] に pack → store
///   中間バッファが int→short で半減し L3 のロードコストも削減。
///
/// 荷重レイアウト: W2[L2][L1×2] (出力優先・各行=512バイト・32バイトアライン)
/// </summary>
internal sealed unsafe class C中間層Int8 : IDisposable
{
    private const int L1x2 = CNNUE評価器HalfKPInt8.L1数 * 2;  // 512
    private const int L2   = CNNUE評価器HalfKPInt8.L2数;       // 64

    private readonly nint  _w2;   // sbyte*(L2 × L1x2), 32バイトアライン
    private readonly short[] _b2; // short[L2]: round(b2_float / dequantScale)

    private bool _disposed;

    internal C中間層Int8(sbyte[] w2, float[] b2, float dequantScale)
    {
        uint sz = (uint)(L2 * L1x2);
        var pw = (sbyte*)NativeMemory.AlignedAlloc(sz, 32);
        fixed (sbyte* src = w2) Unsafe.CopyBlockUnaligned((void*)pw, (void*)src, sz);
        _w2 = (nint)pw;
        _b2 = new short[b2.Length];
        for (int i = 0; i < b2.Length; i++)
            _b2[i] = (short)MathF.Round(b2[i] / dequantScale);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_w2 != 0) NativeMemory.AlignedFree((void*)_w2);
    }

    // ── Forward ──────────────────────────────────────────────────────────────

    /// <summary>uint8[512] → short[L2]（ReLU済み）。dequant_scale は出力層で適用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Forward(byte* h1u8, Span<short> output)
    {
        sbyte* W = (sbyte*)_w2;

        fixed (short* pOut = output)
        fixed (short* pB2  = _b2)
        {
            if (Avx2.IsSupported && Ssse3.IsSupported)
                ForwardAvx2(h1u8, W, pOut, pB2);
            else
                ForwardScalar(h1u8, W, pOut, pB2);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ForwardAvx2(byte* h1u8, sbyte* W, short* output, short* b2)
    {
        var ones  = Vector256.Create((short)1);
        var vZero = Vector256<int>.Zero;

        // 8ニューロンずつ処理し、2回分を vpackssdw で short×16 にまとめて store
        Vector256<int> v_prev = default;
        for (int g = 0; g < L2; g += 8)
        {
            var acc0 = vZero; var acc1 = vZero; var acc2 = vZero; var acc3 = vZero;
            var acc4 = vZero; var acc5 = vZero; var acc6 = vZero; var acc7 = vZero;

            sbyte* w0 = W + (g + 0) * L1x2; sbyte* w1 = W + (g + 1) * L1x2;
            sbyte* w2 = W + (g + 2) * L1x2; sbyte* w3 = W + (g + 3) * L1x2;
            sbyte* w4 = W + (g + 4) * L1x2; sbyte* w5 = W + (g + 5) * L1x2;
            sbyte* w6 = W + (g + 6) * L1x2; sbyte* w7 = W + (g + 7) * L1x2;

            for (int i = 0; i < L1x2; i += 32)
            {
                var a = Avx.LoadVector256(h1u8 + i);
                acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w0 + i)), ones));
                acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w1 + i)), ones));
                acc2 = Avx2.Add(acc2, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w2 + i)), ones));
                acc3 = Avx2.Add(acc3, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w3 + i)), ones));
                acc4 = Avx2.Add(acc4, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w4 + i)), ones));
                acc5 = Avx2.Add(acc5, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w5 + i)), ones));
                acc6 = Avx2.Add(acc6, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w6 + i)), ones));
                acc7 = Avx2.Add(acc7, Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, Avx.LoadAlignedVector256(w7 + i)), ones));
            }

            // short[8] → int32[8] sign-extend (vpmovsxwd) してバイアス加算・ReLU
            var vBias  = Avx2.ConvertToVector256Int32(Sse2.LoadVector128(b2 + g));
            var vHSums = Vector256.Create(
                HSum(acc0), HSum(acc1), HSum(acc2), HSum(acc3),
                HSum(acc4), HSum(acc5), HSum(acc6), HSum(acc7));
            var v = Avx2.Max(vZero, Avx2.Add(vHSums, vBias));

            if ((g & 8) == 0)
            {
                v_prev = v;  // 偶数ブロック: 次の奇数ブロックと一緒に pack
            }
            else
            {
                // vpackssdw: int32[8]+int32[8] → int16[16] (signed saturate)
                // レーン順修正: [p0-3,v0-3 | p4-7,v4-7] → vpermq → [p0-7 | v0-7]
                var packed = Avx2.Permute4x64(
                    Avx2.PackSignedSaturate(v_prev, v).AsInt64(),
                    0b_11_01_10_00);
                Avx2.Store(output + (g - 8), packed.AsInt16());
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ForwardScalar(byte* h1u8, sbyte* W, short* output, short* b2)
    {
        Span<int> acc = stackalloc int[L2];
        acc.Clear();
        for (int i = 0; i < L1x2; i++)
        {
            int v = h1u8[i];
            if (v == 0) continue;
            for (int j = 0; j < L2; j++)
                acc[j] += v * W[j * L1x2 + i];
        }
        for (int j = 0; j < L2; j++)
        {
            int v = acc[j] + b2[j];
            output[j] = v <= 0 ? (short)0 : v >= 32767 ? (short)32767 : (short)v;
        }
    }

    // ── Vector256<int> 水平和 ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HSum(Vector256<int> v)
    {
        var v128 = Sse2.Add(v.GetLower(), v.GetUpper());
        var shuf = Sse2.Shuffle(v128, 0b_10_11_00_01);
        v128 = Sse2.Add(v128, shuf);
        shuf = Sse2.Shuffle(v128, 0b_00_00_10_10);
        v128 = Sse2.Add(v128, shuf);
        return v128.GetElement(0);
    }
}
