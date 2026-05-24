namespace 変成将棋.Models;

// 利きビットボードの計算ユーティリティ。
// Compute全利き：全駒の利きをゼロから計算（テスト・検証用）。
// Compute駒利き：1駒の利きを O(1)/方向 で計算（Compute全利きの内部で使用）。
public static class C利き管理
{
    // ===== 公開メソッド =====

    // 全利きをゼロから再計算（テスト・検証用）
    public static (S利きビット 先手, S利きビット 後手) Compute全利き(C盤面 盤面)
    {
        var 先手 = S利きビット.空;
        var 後手 = S利きビット.空;
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 升 = new S升座標((byte)列, (byte)段);
                var 駒 = 盤面.Get駒(升);
                if (!駒.Is有効) continue;
                var 利き = Compute駒利き(駒.種類, 駒.手番, 升, 盤面);
                if (駒.手番 == E手番.先手) 先手 = 先手.Or(利き);
                else 後手 = 後手.Or(利き);
            }
        }
        return (先手, 後手);
    }

    // 正方向（線形インデックス増加）: 後(1), 左(3), 右後(6), 左後(7)
    // 負方向（線形インデックス減少）: 前(0), 右(2), 右前(4), 左前(5)
    private static readonly bool[] IsPositive方向 = [false, true, false, true, false, false, true, true];

    // 特定の駒1枚の利きをビットボードで計算（O(1) for fixed, O(1)/direction for sliders）
    public static S利きビット Compute駒利き(E駒種 種類, E手番 手番, S升座標 升, C盤面 盤面)
    {
        // 固定移動: O(1) テーブル参照
        var 利き = C到達升テーブル.Get固定利きビット(種類, 手番, 升);

        // スライド移動: O(1)/方向（ブロッカー検出にビット演算使用）
        foreach (int 方向 in GetスライドMethod(種類, 手番))
            利き = 利き.Or(ComputeスライドBit(升, 方向, 盤面.全駒ビット));

        // 獅王: O(1) ビットボード参照
        if (種類 == E駒種.獅王)
        {
            利き = 利き
                .Or(C到達升テーブル.Get獅王隣接ビット(升))
                .Or(C到達升テーブル.Get獅王遠達ビット(升));
        }

        return 利き;
    }

    // スライド方向の利きをビット演算で O(1) 計算する
    private static S利きビット ComputeスライドBit(S升座標 位置, int 方向, S利きビット 全駒)
    {
        var レイ = C到達升テーブル.Getレイビット(方向, 位置);
        var ブロッカー = レイ.And(全駒);
        if (ブロッカー.IsEmpty) return レイ;

        if (IsPositive方向[方向])
        {
            // 正方向：最小インデックスのブロッカーまで（そこを含む）
            return レイ.And(S利きビット.BitsUpTo(ブロッカー.FindFirstBit()));
        }
        else
        {
            // 負方向：最大インデックスのブロッカーから（そこを含む）
            return レイ.And(S利きビット.BitsFrom(ブロッカー.FindLastBit()));
        }
    }

    // ===== 内部実装 =====

    // 駒種と手番からスライド方向を返す
    private static ReadOnlySpan<int> GetスライドMethod(E駒種 種類, E手番 手番)
    {
        if (種類 == E駒種.香車)
        {
            return 手番 == E手番.先手
                ? C駒動き定数.香車先手スライド
                : C駒動き定数.香車後手スライド;
        }
        return 種類 switch
        {
            E駒種.角行 or E駒種.龍馬 => C駒動き定数.角行スライド,
            E駒種.飛車 or E駒種.龍王 => C駒動き定数.飛車スライド,
            E駒種.竪行              => C駒動き定数.竪行スライド,
            _                      => [],
        };
    }
}
