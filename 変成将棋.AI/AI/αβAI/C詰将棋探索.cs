using 変成将棋.Models;

namespace 変成将棋.AI.αβAI;

/// <summary>
/// 反復深化による詰将棋探索（奇数手詰みを 1手→3手→5手… と深化）。
///
/// Or  ノード（攻め方）: 王手手のうち少なくとも一つで詰めば詰み
/// And ノード（受け方）: 全合法手で詰まされれば詰み
///
/// 打歩詰め排除は Get合法手 内の Filter打歩詰 に委ねる。
/// </summary>
public static class C詰将棋探索
{
    public const int デフォルト最大手数 = 5;

    [System.ThreadStatic] private static long _nodes;
    public static long LastNodes => _nodes;

    /// <summary>
    /// 指定手数以内の詰みを探索する。詰みなら (手数, 詰み初手) を返す、なければ null。
    /// </summary>
    public static (int 手数, S手 初手)? 詰み探索(C盤面 盤面, int 最大手数 = デフォルト最大手数, CancellationToken ct = default)
    {
        _nodes = 0;
        for (int 手数 = 1; 手数 <= 最大手数; 手数 += 2)
        {
            if (ct.IsCancellationRequested) return null;
            var 初手 = Or詰み(盤面, 手数, ct);
            if (初手.HasValue) return (手数, 初手.Value);
        }
        return null;
    }

    /// <summary>
    /// 高速1手詰み判定。反復深化・再帰なしの専用実装。
    /// 詰み手を返す、なければ null。
    /// </summary>
    public static S手? Get1手詰み(C盤面 盤面)
    {
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        Span<S手> 受け = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);

        for (int i = 0; i < n; i++)
        {
            var 取消 = 盤面.Apply(buf[i]);

            // 相手玉を直接取れる手 → 即詰み
            if (!盤面.Find玉(盤面.手番).Is盤内)
            {
                盤面.Undo(buf[i], 取消);
                return buf[i];
            }

            // 王手でなければスキップ
            if (C合法手生成器.Is自玉安全(盤面, 盤面.手番))
            {
                盤面.Undo(buf[i], 取消);
                continue;
            }

            // 受け方の合法手が0なら詰み
            int m = C合法手生成器.Get合法手(盤面, 受け);
            盤面.Undo(buf[i], 取消);

            if (m == 0) return buf[i];
        }
        return null;
    }

    // 攻め方のターン: 王手手のうち少なくとも一つで詰むか。詰む手を返す、なければ null。
    private static S手? Or詰み(C盤面 盤面, int 残手数, CancellationToken ct)
    {
        _nodes++;
        if (残手数 <= 0 || ct.IsCancellationRequested) return null;

        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);

        for (int i = 0; i < n; i++)
        {
            var 取消 = 盤面.Apply(buf[i]);

            // Apply後の手番 = 受け方。受け方の玉が安全 = 王手でない → スキップ
            if (C合法手生成器.Is自玉安全(盤面, 盤面.手番))
            {
                盤面.Undo(buf[i], 取消);
                continue;
            }

            // 相手玉を取った = 即詰み
            if (!盤面.Find玉(盤面.手番).Is盤内)
            {
                盤面.Undo(buf[i], 取消);
                return buf[i];
            }

            bool result = And詰み(盤面, 残手数 - 1, ct);
            盤面.Undo(buf[i], 取消);
            if (result) return buf[i];
        }
        return null;
    }

    // 受け方のターン: 全合法手で詰まされるか
    private static bool And詰み(C盤面 盤面, int 残手数, CancellationToken ct)
    {
        _nodes++;
        if (ct.IsCancellationRequested) return false;

        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);

        if (n == 0) return true;        // 合法手なし = 詰み
        if (残手数 == 0) return false;  // 探索限界 = 詰みとみなさない

        for (int i = 0; i < n; i++)
        {
            var 取消 = 盤面.Apply(buf[i]);
            bool result = Or詰み(盤面, 残手数 - 1, ct).HasValue;
            盤面.Undo(buf[i], 取消);
            if (!result) return false;  // この手で逃げられる
        }
        return true;  // 全手詰む
    }
}
