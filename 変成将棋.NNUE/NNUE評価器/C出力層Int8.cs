using System.Numerics;
using System.Runtime.CompilerServices;

namespace 変成将棋.NNUE;

/// <summary>
/// INT8評価器用出力層。short[L2] → スカラー。
/// dequantScale を保持し、short → float 変換と重み乗算を同時に行う。
/// </summary>
internal sealed class C出力層Int8
{
    private static readonly int _SIMD幅float = Vector<float>.Count;

    private readonly float[] _w3;
    private readonly float   _b3;
    private readonly float   _dequantScale;

    internal C出力層Int8(float[] w3, float b3, float dequantScale)
    {
        _w3           = w3;
        _b3           = b3;
        _dequantScale = dequantScale;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal float Forward(Span<short> L2出力)
    {
        int   fw    = _SIMD幅float;
        int   sw    = fw * 2;        // Vector<short>.Count = fw*2
        float score = _b3;
        float dq    = _dequantScale;

        var vDq  = new Vector<float>(dq);
        var vSum = Vector<float>.Zero;
        int k    = 0;

        // short[sw] → int[fw]+int[fw] → float[fw] × dq × w3[fw]
        for (; k <= CNNUE評価器HalfKPInt8.L2数 - sw; k += sw)
        {
            Vector.Widen(new Vector<short>(L2出力[k..]), out var vIntLo, out var vIntHi);
            vSum += Vector.ConvertToSingle(vIntLo) * vDq * new Vector<float>(_w3, k);
            vSum += Vector.ConvertToSingle(vIntHi) * vDq * new Vector<float>(_w3, k + fw);
        }
        score += Vector.Dot(vSum, Vector<float>.One);
        for (; k < CNNUE評価器HalfKPInt8.L2数; k++)
            score += (float)L2出力[k] * dq * _w3[k];
        return score;
    }
}
