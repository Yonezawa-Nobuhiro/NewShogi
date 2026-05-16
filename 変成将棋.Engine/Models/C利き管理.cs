namespace 変成将棋.Models;

// 利きビットボードの計算と差分更新を担うクラス。
// C盤面.Apply/Undo から RemoveOld（盤面修正前）、AddNew（盤面修正後）の順で呼ぶ。
// 影響を受ける升の周辺（8方向レイ）だけを再計算するため O(72) 程度で完了する。
public static class C利き管理
{
    // 方向の逆方向テーブル（前↔後, 右↔左, 右前↔左後, 左前↔右後）
    private static readonly int[] 逆方向 =
        [C到達升テーブル.後, C到達升テーブル.前, C到達升テーブル.左, C到達升テーブル.右,
         C到達升テーブル.左後, C到達升テーブル.右後, C到達升テーブル.左前, C到達升テーブル.右前];

    // ===== 公開メソッド =====

    // 全利きをゼロから再計算（初期化・デバッグ用）
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
                if (駒 == null) continue;
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

    // 盤面修正前に呼ぶ：影響範囲の古い利きを除去する
    public static void RemoveOld(S手 手, C盤面 盤面,
        ref S利きビット 先手利き, ref S利きビット 後手利き)
        => UpdateRange(手, 盤面, ref 先手利き, ref 後手利き, remove: true);

    // 盤面修正後に呼ぶ：影響範囲の新しい利きを追加する
    public static void AddNew(S手 手, C盤面 盤面,
        ref S利きビット 先手利き, ref S利きビット 後手利き)
        => UpdateRange(手, 盤面, ref 先手利き, ref 後手利き, remove: false);

    // ===== 内部実装 =====

    // 影響を受ける升とその8方向レイ上の全駒の利きを更新する
    private static void UpdateRange(S手 手, C盤面 盤面,
        ref S利きビット 先手利き, ref S利きビット 後手利き, bool remove)
    {
        // 処理済みフラグ（同じ駒を二重に処理しない）
        Span<bool> 処理済み = stackalloc bool[81];

        // 影響升の収集（元升・中間升・先升）
        Span<S升座標> 影響升 = stackalloc S升座標[3];
        int 影響数 = 0;

        if (!手.Is打ち)     影響升[影響数++] = 手.Get移動元;
        if (手.Is獅王2回移動) 影響升[影響数++] = 手.Get中間;
        影響升[影響数++] = 手.Get移動先;

        for (int i = 0; i < 影響数; i++)
        {
            var 升 = 影響升[i];

            // 升自身の駒を処理
            ProcessSquare(升, 盤面, ref 先手利き, ref 後手利き, remove, 処理済み);

            // 8方向のレイ上にある駒を処理（走り駒のshadow更新が目的）
            for (int 方向 = 0; 方向 < 8; 方向++)
            {
                foreach (byte rb in C到達升テーブル.Getスライドレイ(方向, 升))
                {
                    var 先 = new S升座標(rb);
                    ProcessSquare(先, 盤面, ref 先手利き, ref 後手利き, remove, 処理済み);
                    if (盤面.Get駒(先) != null) break; // 最初の駒より先は影響なし
                }
            }
        }
    }

    // 指定升の駒の利きを追加または除去する（処理済みチェック付き）
    private static void ProcessSquare(
        S升座標 升, C盤面 盤面,
        ref S利きビット 先手利き, ref S利きビット 後手利き,
        bool remove, Span<bool> 処理済み)
    {
        int idx = 升.線形インデックス;
        if (処理済み[idx]) return;
        処理済み[idx] = true;

        var 駒 = 盤面.Get駒(升);
        if (駒 == null) return;

        var 利き = Compute駒利き(駒.種類, 駒.手番, 升, 盤面);
        if (駒.手番 == E手番.先手)
        {
            先手利き = remove
                ? 先手利き.Xor(先手利き.And(利き)) // A AND NOT B
                : 先手利き.Or(利き);
        }
        else
        {
            後手利き = remove
                ? 後手利き.Xor(後手利き.And(利き))
                : 後手利き.Or(利き);
        }
    }

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
