using System.Numerics;
using System.Runtime.CompilerServices;
using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>出力層 - L2（L2数次元）→ スカラー評価値（centipawn）。</summary>
internal sealed class C出力層
{
    private static readonly int _SIMD幅 = Vector<float>.Count;

    private readonly float[] _出力層荷重;  // [L2数]
    private readonly float   _出力層切片;

    internal C出力層(float[] 出力層荷重, float 出力層切片)
    {
        _出力層荷重 = 出力層荷重;
        _出力層切片 = 出力層切片;
    }

    /// <summary>L2出力 → 評価スカラー（scale 前の生値を返す）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal float Forward(Span<float> L2出力)
    {
        int SIMD幅      = _SIMD幅;
        float 評価値    = _出力層切片;
        var スコアベクトル = Vector<float>.Zero;
        int k = 0;
        for (; k <= CNNUE評価器.L2数 - SIMD幅; k += SIMD幅)
            スコアベクトル += new Vector<float>(L2出力[k..]) * new Vector<float>(_出力層荷重, k);
        for (int m = 0; m < SIMD幅; m++)
            評価値 += スコアベクトル[m];
        for (; k < CNNUE評価器.L2数; k++)
            評価値 += L2出力[k] * _出力層荷重[k];
        return 評価値;
    }
}
