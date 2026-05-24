using System.Numerics;
using System.Runtime.CompilerServices;

namespace 変成将棋.NNUE;

/// <summary>
/// INT8評価器用出力層。L2(float[L2数]) → スカラー。
/// 荷重は float のまま保持（L2=64要素で速度差が小さいため）。
/// float版 C出力層 と同じ実装だが独立クラスとして保持。
/// </summary>
internal sealed class C出力層Int8
{
    private static readonly int _SIMD幅 = Vector<float>.Count;

    private readonly float[] _w3;
    private readonly float   _b3;

    internal C出力層Int8(float[] w3, float b3) { _w3 = w3; _b3 = b3; }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal float Forward(Span<float> L2出力)
    {
        int SIMD幅 = _SIMD幅;
        float score = _b3;
        var vSum = Vector<float>.Zero;
        int k = 0;
        for (; k <= CNNUE評価器HalfKPInt8.L2数 - SIMD幅; k += SIMD幅)
            vSum += new Vector<float>(L2出力[k..]) * new Vector<float>(_w3, k);
        for (int m = 0; m < SIMD幅; m++) score += vSum[m];
        for (; k < CNNUE評価器HalfKPInt8.L2数; k++)
            score += L2出力[k] * _w3[k];
        return score;
    }
}
