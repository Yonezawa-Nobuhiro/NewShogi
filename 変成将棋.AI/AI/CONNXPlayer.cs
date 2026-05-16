using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using 変成将棋.Models;

namespace 変成将棋.AI;

// ONNX モデルを使ったグリーディ方策プレイヤー（MCTS なし）。
// 合法手のうちポリシーロジットが最大の手を選ぶ。
public sealed class CONNXPlayer : IプレイヤーAI, IDisposable
{
    private readonly InferenceSession _session;

    public CONNXPlayer(string modelPath)
    {
        _session = new InferenceSession(modelPath);
    }

    public S手? Get手(C盤面 盤面)
    {
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        if (n == 0) return null;

        float[] policy = Infer(CPython連携.盤面テンソル(盤面));

        S手 best = buf[0];
        float bestLogit = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float logit = policy[CPython連携.手インデックス(buf[i])];
            if (logit > bestLogit) { bestLogit = logit; best = buf[i]; }
        }
        return best;
    }

    internal float[] Infer(float[] boardTensor)
    {
        var inputTensor = new DenseTensor<float>(boardTensor, [1, 47, 9, 9]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", inputTensor) };
        using var outputs = _session.Run(inputs);
        return outputs[0].AsTensor<float>().ToArray();
    }

    public void Dispose() => _session.Dispose();
}
