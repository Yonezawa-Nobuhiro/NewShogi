using 変成将棋.Models;

namespace 変成将棋.AI;

// 合法手からランダムに1手選ぶ最弱AI。
public sealed class CランダムAI : IプレイヤーAI
{
    private static readonly Random 乱数 = new();

    public S手? Get手(C盤面 盤面)
    {
        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];
        int 手数 = C合法手生成器.Get合法手(盤面, バッファ);
        if (手数 == 0) return null;
        return バッファ[乱数.Next(手数)];
    }

    public void Dispose() { }   // リソースなし
}
