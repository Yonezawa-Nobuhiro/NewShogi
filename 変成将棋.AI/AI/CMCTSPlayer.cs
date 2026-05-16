using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using 変成将棋.Models;

namespace 変成将棋.AI;

// AlphaZero 方式の MCTS + ONNX プレイヤー。
// PUCT 式: Q(s,a) + C_puct * P(s,a) * sqrt(N(s)) / (1 + N(s,a))
//   Q は親視点（= -child.QValue）で計算する。
public sealed class CMCTSPlayer : IプレイヤーAI, IDisposable
{
    private const float C_PUCT = 1.25f;

    private readonly InferenceSession _session;
    public int NumSimulations { get; set; }

    public CMCTSPlayer(string modelPath, int numSimulations = 50)
    {
        _session      = new InferenceSession(modelPath);
        NumSimulations = numSimulations;
    }

    // ===== 公開 API =====

    public S手? Get手(C盤面 盤面)
    {
        var root = new Node { Sfen = 盤面.ToSFEN() };

        for (int i = 0; i < NumSimulations; i++)
        {
            var path = new List<Node>(32);
            var leaf = Select(root, path);
            float value = ExpandAndEval(leaf);
            Backup(path, value);
        }

        if (root.Children.Count == 0) return null;
        return root.Children.Values.MaxBy(c => c.VisitCount)!.Move;
    }

    // ===== MCTS ステップ =====

    private static Node Select(Node root, List<Node> path)
    {
        var node = root;
        path.Add(node);
        while (node.IsExpanded && !node.IsTerminal && node.Children.Count > 0)
        {
            float sqrtN = MathF.Sqrt(node.VisitCount);
            node = node.Children.Values.MaxBy(c => c.PuctScore(sqrtN))!;
            // 遅延 SFEN 計算（初回訪問時のみ）
            node.Sfen ??= ApplyMove(node.Parent!.Sfen!, node.Move!.Value);
            path.Add(node);
        }
        return node;
    }

    private float ExpandAndEval(Node node)
    {
        node.IsExpanded = true;

        var 盤面 = new C盤面(node.Sfen!);
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);

        if (n == 0)
        {
            node.IsTerminal    = true;
            node.TerminalValue = -1f;   // 手番側（合法手なし）= 負け
            return -1f;
        }

        var (policy, value) = Infer(CPython連携.盤面テンソル(盤面));

        for (int i = 0; i < n; i++)
        {
            int idx = CPython連携.手インデックス(buf[i]);
            node.Children[idx] = new Node
            {
                Parent = node,
                Move   = buf[i],
                Prior  = policy[idx],
            };
        }
        return value;
    }

    private static void Backup(List<Node> path, float value)
    {
        for (int i = path.Count - 1; i >= 0; i--)
        {
            path[i].VisitCount++;
            path[i].ValueSum += value;
            value = -value;
        }
    }

    // ===== 推論・ユーティリティ =====

    private (float[] policy, float value) Infer(float[] boardTensor)
    {
        var inputTensor = new DenseTensor<float>(boardTensor, [1, 47, 9, 9]);
        var inputs = new[] { NamedOnnxValue.CreateFromTensor("input", inputTensor) };
        using var outputs = _session.Run(inputs);
        float[] policy = outputs[0].AsTensor<float>().ToArray();
        float value    = outputs[1].AsTensor<float>().ToArray()[0];
        return (policy, value);
    }

    private static string ApplyMove(string sfen, S手 手)
    {
        var 盤面 = new C盤面(sfen);
        盤面.Apply(手);
        return 盤面.ToSFEN();
    }

    public void Dispose() => _session.Dispose();

    // ===== 内部ノードクラス =====

    private sealed class Node
    {
        public string? Sfen;
        public Node? Parent;
        public S手? Move;
        public float Prior;
        public Dictionary<int, Node> Children = [];
        public int VisitCount;
        public float ValueSum;
        public bool IsExpanded;
        public bool IsTerminal;
        public float TerminalValue;

        public float QValue => VisitCount > 0 ? ValueSum / VisitCount : 0f;

        public float PuctScore(float sqrtParentVisits)
        {
            float q = -QValue;
            float u = C_PUCT * Prior * sqrtParentVisits / (1 + VisitCount);
            return q + u;
        }
    }
}
