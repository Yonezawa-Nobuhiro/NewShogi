using System.Numerics;
using System.Runtime.CompilerServices;
using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>中間層 - 結合L1（L1数×2次元）→ L2（L2数次元）+ ReLU。</summary>
internal sealed class C中間層
{
    private static readonly int _SIMD幅 = Vector<float>.Count;

    private readonly float[] _中間層荷重;  // [(L1数×2) × L2数]
    private readonly float[] _中間層切片;  // [L2数]

    internal C中間層(float[] 中間層荷重, float[] 中間層切片)
    {
        _中間層荷重 = 中間層荷重;
        _中間層切片 = 中間層切片;
    }

    /// <summary>結合L1 → L2出力（ReLU 込み）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void Forward(Span<float> 結合L1, Span<float> L2出力)
    {
        int SIMD幅  = _SIMD幅;
        int L1結合数 = CNNUE評価器.L1数 * 2;
        var ゼロ    = Vector<float>.Zero;

        _中間層切片.CopyTo(L2出力);

        for (int i = 0; i < L1結合数; i++)
        {
            float L1値 = 結合L1[i];
            if (L1値 == 0f) continue;
            var SIMD値     = new Vector<float>(L1値);
            int L2開始番号 = i * CNNUE評価器.L2数;
            int j = 0;
            for (; j <= CNNUE評価器.L2数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(L2出力[j..]) + SIMD値 * new Vector<float>(_中間層荷重, L2開始番号 + j))
                    .CopyTo(L2出力[j..]);
            for (; j < CNNUE評価器.L2数; j++)
                L2出力[j] += L1値 * _中間層荷重[L2開始番号 + j];
        }

        // ReLU
        for (int i = 0; i <= CNNUE評価器.L2数 - SIMD幅; i += SIMD幅)
            Vector.Max(new Vector<float>(L2出力[i..]), ゼロ).CopyTo(L2出力[i..]);
        for (int i = (CNNUE評価器.L2数 / SIMD幅) * SIMD幅; i < CNNUE評価器.L2数; i++)
            if (L2出力[i] < 0) L2出力[i] = 0;
    }
}
