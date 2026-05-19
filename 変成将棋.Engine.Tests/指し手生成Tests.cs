using 変成将棋.Models;

namespace 変成将棋.Engine.Tests;

/// <summary>
/// C合法手生成器.Generate擬似合法手 の駒種別・条件別テスト。
///
/// 各テストは最小限の局面（テスト対象の駒1枚 + 両玉）で確認する。
/// 先手玉 = (1,9)、後手玉 = (9,1) として利き・王手放置の影響を避ける。
/// Generate擬似合法手（王手放置チェックなし）を使用して移動生成のみ検証する。
/// </summary>
public class 指し手生成Tests
{
    private static S升座標 Sq(int 列, int 段) => new((byte)列, (byte)段);

    // 指定した升からの移動手一覧（打ち手を除く）
    private static List<S手> Get移動手(C盤面 盤面, S升座標 元)
    {
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Generate擬似合法手(盤面, buf);
        var list = new List<S手>();
        for (int i = 0; i < n; i++)
            if (!buf[i].Is打ち && buf[i].Get移動元.Equals(元))
                list.Add(buf[i]);
        return list;
    }

    // 盤面に指定した先手の駒を追加した局面（先手玉(1,9)・後手玉(9,1)固定）
    // SFEN: "k8/9/9/9/9/9/9/9/8K b - 1" に駒を加えた形
    private static C盤面 作る(int 列, int 段, E駒種 駒種, E手番 手番 = E手番.先手,
        string 持ち駒 = "-")
    {
        // 先手玉(1,9)・後手玉(9,1) は試験駒の移動に干渉しない位置
        // 試験駒と王が衝突する場合は呼び出し側でSFENを直接指定する
        var 盤面 = new C盤面("k8/9/9/9/9/9/9/9/8K b - 1");
        盤面.Set駒(列, 段, 駒種, 手番);
        return 盤面;
    }

    // ─── 歩兵 ────────────────────────────────────────────────────────

    [Fact]
    public void 歩兵_前進1マス()
    {
        var 盤面 = 作る(5, 5, E駒種.歩兵);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        Assert.Equal(1, 手一覧.Count);
        Assert.Equal(Sq(5, 4), 手一覧[0].Get移動先);
        Assert.False(手一覧[0].Is成り); // 敵陣外→不成りのみ
    }

    [Fact]
    public void 歩兵_敵陣内から成りと不成りの両方()
    {
        // 先手歩が段3（敵陣）にいて段2へ→成り+不成り
        var 盤面 = 作る(5, 3, E駒種.歩兵);
        var 手一覧 = Get移動手(盤面, Sq(5, 3));

        Assert.Equal(2, 手一覧.Count);
        Assert.Contains(手一覧, 手 => 手.Is成り && 手.Get移動先.Equals(Sq(5, 2)));
        Assert.Contains(手一覧, 手 => !手.Is成り && 手.Get移動先.Equals(Sq(5, 2)));
    }

    [Fact]
    public void 歩兵_最終段への移動は成り義務()
    {
        // 先手歩が段2にいて段1（最終段）へ→成りのみ
        var 盤面 = 作る(5, 2, E駒種.歩兵);
        var 手一覧 = Get移動手(盤面, Sq(5, 2));

        Assert.Equal(1, 手一覧.Count);
        Assert.True(手一覧[0].Is成り);
        Assert.Equal(Sq(5, 1), 手一覧[0].Get移動先);
    }

    [Fact]
    public void 歩兵_後手の歩は段が増加する方向()
    {
        var 盤面 = new C盤面("k8/9/9/9/4p4/9/9/9/8K w - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        Assert.Equal(1, 手一覧.Count);
        Assert.Equal(Sq(5, 6), 手一覧[0].Get移動先); // 後手は段増加
    }

    [Fact]
    public void 歩兵_前に自駒があれば移動不可()
    {
        var 盤面 = 作る(5, 5, E駒種.歩兵);
        盤面.Set駒(5, 4, E駒種.銀将, E手番.先手); // 前を自駒でブロック
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.Equal(0, 手一覧.Count);
    }

    [Fact]
    public void 歩兵_前に相手駒があれば取れる()
    {
        var 盤面 = 作る(5, 5, E駒種.歩兵);
        盤面.Set駒(5, 4, E駒種.銀将, E手番.後手); // 前に相手駒
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.Equal(1, 手一覧.Count);
        Assert.Equal(Sq(5, 4), 手一覧[0].Get移動先);
    }

    // ─── 香車 ────────────────────────────────────────────────────────

    [Fact]
    public void 香車_段5からの手数は6()
    {
        // 段5から: 段4(不成り1), 段3(成り+不成り2), 段2(成り+不成り2), 段1(義務成り1) = 6手
        var 盤面 = 作る(5, 5, E駒種.香車);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.Equal(6, 手一覧.Count);
    }

    [Fact]
    public void 香車_段1への移動は成り義務()
    {
        var 盤面 = 作る(5, 5, E駒種.香車);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        var 段1への手 = 手一覧.Where(手 => 手.Get移動先.段 == 1).ToList();
        Assert.Equal(1, 段1への手.Count);
        Assert.True(段1への手[0].Is成り);
    }

    [Fact]
    public void 香車_途中に相手駒があれば取ってそこで止まる()
    {
        // 段5の香車、段3に相手駒
        var 盤面 = 作る(5, 5, E駒種.香車);
        盤面.Set駒(5, 3, E駒種.銀将, E手番.後手);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 段4(不成り1) + 段3(成り+不成り2) = 3手
        Assert.Equal(3, 手一覧.Count);
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.段 < 3);
    }

    [Fact]
    public void 香車_後手は段増加方向のみ()
    {
        var 盤面 = new C盤面("k8/9/9/9/4l4/9/9/9/8K w - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        Assert.All(手一覧, 手 => Assert.True(手.Get移動先.段 > 5));
    }

    // ─── 桂馬 ────────────────────────────────────────────────────────

    [Fact]
    public void 桂馬_段5からは両端2升に跳ぶ()
    {
        // 段5の桂馬→段3の列4と列6に跳ぶ（どちらも敵陣→成り+不成り = 4手）
        var 盤面 = 作る(5, 5, E駒種.桂馬);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        Assert.Equal(4, 手一覧.Count);
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 3)) && 手.Is成り);
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 3)) && !手.Is成り);
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 3)) && 手.Is成り);
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 3)) && !手.Is成り);
    }

    [Fact]
    public void 桂馬_段1への移動は成り義務()
    {
        // 段3の桂馬→段1（最終段）→成りのみ
        var 盤面 = 作る(5, 3, E駒種.桂馬);
        var 手一覧 = Get移動手(盤面, Sq(5, 3));
        Assert.Equal(2, 手一覧.Count);
        Assert.All(手一覧, 手 => Assert.True(手.Is成り));
        Assert.All(手一覧, 手 => Assert.Equal(1, (int)手.Get移動先.段));
    }

    [Fact]
    public void 桂馬_段2からは手がない_行き所なし()
    {
        // 段2の桂馬→段0は盤外 → 0手
        var 盤面 = 作る(5, 2, E駒種.桂馬);
        var 手一覧 = Get移動手(盤面, Sq(5, 2));
        Assert.Equal(0, 手一覧.Count);
    }

    [Fact]
    public void 桂馬_端の列は片側のみ()
    {
        // 列1の桂馬→列-1は盤外 → 列2への1方向のみ
        var 盤面 = 作る(1, 5, E駒種.桂馬);
        var 手一覧 = Get移動手(盤面, Sq(1, 5));
        Assert.Equal(2, 手一覧.Count); // (2,3) の成り+不成り
        Assert.All(手一覧, 手 => Assert.Equal(2, (int)手.Get移動先.列));
    }

    // ─── 銀将 ────────────────────────────────────────────────────────

    [Fact]
    public void 銀将_段5から5方向に動ける()
    {
        var 盤面 = 作る(5, 5, E駒種.銀将);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.Equal(5, 手一覧.Count);
    }

    [Fact]
    public void 銀将_敵陣内外をまたぐ移動で成りと不成りが生成される()
    {
        // 段4の銀将（敵陣外）から斜め前（段3=敵陣）へ → 成り+不成り
        var 盤面 = 作る(5, 4, E駒種.銀将);
        var 手一覧 = Get移動手(盤面, Sq(5, 4));

        // 段3への移動: (4,3), (5,3), (6,3) → 各2手 = 6手
        // 段5への移動: (4,5), (6,5) → 各1手 = 2手
        // 合計: 8手
        Assert.Equal(8, 手一覧.Count);

        // 前方の3升はすべて成り手と不成り手がある
        foreach (int 列 in new[] { 4, 5, 6 })
        {
            Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(列, 3)) && 手.Is成り);
            Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(列, 3)) && !手.Is成り);
        }
    }

    [Fact]
    public void 銀将_後手の前方は段増加()
    {
        var 盤面 = new C盤面("k8/9/9/9/4s4/9/9/9/8K w - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        // 後手銀: 斜め後ろは段5の斜め（段4方向）、前は段6方向
        // 後手銀将の移動方向: [(-1,-1),(+1,-1),(-1,+1),(+1,+1),(0,+1)]
        Assert.Equal(5, 手一覧.Count);
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(5, 6)));
    }

    // ─── 金将 ────────────────────────────────────────────────────────

    [Fact]
    public void 金将_段5から6方向に動ける()
    {
        var 盤面 = 作る(5, 5, E駒種.金将);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.Equal(6, 手一覧.Count);
    }

    [Fact]
    public void 金将_斜め後ろには動けない()
    {
        var 盤面 = 作る(5, 5, E駒種.金将);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        // 右後(4,6)と左後(6,6)は金将の移動先に含まれない
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 6)));
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 6)));
    }

    [Fact]
    public void 金将_敵陣への移動で成りが生成される()
    {
        // 段4の金将から段3（敵陣）へ → 成り（鳳凰）+不成り
        var 盤面 = 作る(5, 4, E駒種.金将);
        var 手一覧 = Get移動手(盤面, Sq(5, 4));
        Assert.Contains(手一覧, 手 => 手.Is成り && 手.Get移動先.段 == 3);
        Assert.Contains(手一覧, 手 => !手.Is成り && 手.Get移動先.段 == 3);
    }

    // ─── 角行・龍馬 ──────────────────────────────────────────────────

    [Fact]
    public void 角行_斜め4方向にスライド()
    {
        // 角行(5,5): 各方向4升 × 4方向 = 最大16升。両玉は干渉しない配置で確認
        var 盤面 = new C盤面("9/9/9/9/4B4/9/9/9/9 b - 1"); // 玉なしで擬似合法手のみ
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        // 4方向各4升 = 16升、敵陣(段1-3)への手は成り+不成りで増える
        // 右前: (4,4)不成り, (3,3)成り+不成り, (2,2)成り+不成り, (1,1)成り+不成り = 7手
        // 左前: (6,4)不成り, (7,3)成り+不成り, (8,2)成り+不成り, (9,1)成り+不成り = 7手
        // 右後: (4,6)(3,7)(2,8)(1,9) すべて不成り = 4手
        // 左後: (6,6)(7,7)(8,8)(9,9) すべて不成り = 4手
        // 合計: 7+7+4+4 = 22手
        Assert.Equal(22, 手一覧.Count);
    }

    [Fact]
    public void 角行_ブロッカーで止まる()
    {
        var 盤面 = 作る(5, 5, E駒種.角行);
        盤面.Set駒(3, 3, E駒種.銀将, E手番.後手); // 右前方向にブロッカー
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // (4,4)不成り, (3,3)相手駒取り（成り+不成り） の3手のみ（右前方向）
        var 右前 = 手一覧.Where(手 => 手.Get移動先.列 < 5 && 手.Get移動先.段 < 5).ToList();
        Assert.Equal(3, 右前.Count);
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(2, 2)));
    }

    [Fact]
    public void 龍馬_斜めスライドに縦横1升が追加される()
    {
        var 盤面 = 作る(5, 5, E駒種.龍馬);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 縦横1升（直交4升）が追加される
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(5, 4)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(5, 6)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 5)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 5)));
    }

    // ─── 飛車・龍王 ──────────────────────────────────────────────────

    [Fact]
    public void 飛車_縦横4方向にスライド()
    {
        var 盤面 = new C盤面("9/9/9/9/4R4/9/9/9/9 b - 1"); // 玉なし
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        // 横: (1,5)〜(4,5) = 4手, (6,5)〜(9,5) = 4手 → すべて不成り × 8
        // 縦上: (5,4)不成り, (5,3)成り+不成り, (5,2)成り+不成り, (5,1)成り+不成り = 7手
        // 縦下: (5,6)(5,7)(5,8)(5,9) 不成り × 4
        // 合計: 8 + 7 + 4 = 19手
        Assert.Equal(19, 手一覧.Count);
    }

    [Fact]
    public void 龍王_縦横スライドに斜め1升が追加される()
    {
        var 盤面 = 作る(5, 5, E駒種.龍王);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        // 斜め4升（直交固定部分）が追加される
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 4)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 4)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 6)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 6)));
    }

    // ─── 竪行 ────────────────────────────────────────────────────────

    [Fact]
    public void 竪行_縦スライドと横1升()
    {
        var 盤面 = 作る(5, 5, E駒種.竪行);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 竪行は成駒なので成れない（Is成れる = false） → 全て不成り1手
        // 縦上: 段4,3,2,1 = 4手
        // 縦下: 段6,7,8,9 = 4手（先手玉(1,9)は列1なので(5,9)は空き）
        // 横: (4,5),(6,5) = 2手
        // 合計: 4 + 4 + 2 = 10手
        Assert.Equal(10, 手一覧.Count);

        // 横移動
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 5)));
        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 5)));
        // 斜めには動けない
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 4)));
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(6, 4)));
    }

    // ─── 騎兵 ────────────────────────────────────────────────────────

    [Fact]
    public void 騎兵_8方向に跳べる()
    {
        // 騎兵(5,5): チェスナイト8方向すべて
        var 盤面 = 作る(5, 5, E駒種.騎兵);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        var 期待 = new[] {
            Sq(4, 3), Sq(6, 3), Sq(3, 4), Sq(7, 4),
            Sq(3, 6), Sq(7, 6), Sq(4, 7), Sq(6, 7)
        };
        foreach (var 先 in 期待)
            Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(先));
    }

    [Fact]
    public void 騎兵_盤外は除外される()
    {
        // 騎兵が角に近い場合、盤外への移動は生成されない
        var 盤面 = 作る(1, 1, E駒種.騎兵);
        var 手一覧 = Get移動手(盤面, Sq(1, 1));
        Assert.All(手一覧, 手 => Assert.True(手.Get移動先.Is盤内));
    }

    [Fact]
    public void 騎兵_自駒があれば除外される()
    {
        var 盤面 = 作る(5, 5, E駒種.騎兵);
        盤面.Set駒(4, 3, E駒種.歩兵, E手番.先手); // 移動先に自駒
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 3)));
    }

    // ─── 麒麟 ────────────────────────────────────────────────────────

    [Fact]
    public void 麒麟_斜め1升と縦横2升跳び()
    {
        var 盤面 = 作る(5, 5, E駒種.麒麟);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 麒麟は成駒なので成れない（Is成れる = false） → 全て不成り1手
        // 斜め1升: (4,4),(6,4),(4,6),(6,6) = 4手
        // 縦横2升: (5,3),(5,7),(3,5),(7,5) = 4手
        // 合計: 4 + 4 = 8手
        Assert.Equal(8, 手一覧.Count);

        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(5, 3)) && !手.Is成り);
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(5, 2)));  // 2升先は動けない
    }

    // ─── 鳳凰 ────────────────────────────────────────────────────────

    [Fact]
    public void 鳳凰_縦横1升と斜め2升跳び()
    {
        var 盤面 = 作る(5, 5, E駒種.鳳凰);
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 鳳凰は成駒なので成れない（Is成れる = false） → 全て不成り1手
        // 縦横1升: (5,4),(5,6),(4,5),(6,5) = 4手
        // 斜め2升: (3,3),(7,3),(3,7),(7,7) = 4手
        // 合計: 4 + 4 = 8手
        Assert.Equal(8, 手一覧.Count);

        Assert.Contains(手一覧, 手 => 手.Get移動先.Equals(Sq(3, 3)) && !手.Is成り);
        Assert.DoesNotContain(手一覧, 手 => 手.Get移動先.Equals(Sq(4, 4))); // 斜め1升は動けない
    }

    // ─── 玉将 ────────────────────────────────────────────────────────

    [Fact]
    public void 玉将_8方向に1升()
    {
        var 盤面 = new C盤面("k8/9/9/9/4K4/9/9/9/9 b - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));
        Assert.Equal(8, 手一覧.Count);
    }

    [Fact]
    public void 玉将_敵陣内でも成り条件未達なら成れない()
    {
        // 先手玉が敵陣（段2）にいても、後手玉将が自陣3段目以内でなければ成れない
        var 盤面 = new C盤面("9/9/9/9/9/9/9/k8/4K4 b - 1");
        // 後手玉は (9,8)（自陣外）→ 成り条件未達
        var 手一覧 = Get移動手(盤面, Sq(5, 9));
        Assert.All(手一覧, 手 => Assert.False(手.Is成り));
    }

    [Fact]
    public void 玉将_両条件を満たすと成れる()
    {
        // 先手玉(5,3)=敵陣内、後手玉将(5,7)=自陣3段目以内
        var 盤面 = new C盤面("9/9/4K4/9/9/9/4k4/9/9 b - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 3));
        Assert.Contains(手一覧, 手 => 手.Is成り);
    }

    [Fact]
    public void 玉将_相手が獅王なら成れない()
    {
        // 相手が既に獅王に成っている場合は玉将は成れない
        var 盤面 = new C盤面("9/9/4K4/9/9/9/4+k4/9/9 b - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 3));
        Assert.All(手一覧, 手 => Assert.False(手.Is成り));
    }

    // ─── 獅王 ────────────────────────────────────────────────────────

    [Fact]
    public void 獅王_タイプA1回移動は8方向()
    {
        var 盤面 = new C盤面("k8/9/9/9/4+K4/9/9/9/9 b - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 通常手（Is獅王2回移動=false）は 1回移動 + タイプBジャンプ
        var 通常手 = 手一覧.Where(手 => !手.Is獅王2回移動).ToList();
        // 隣接8升 + タイプB（距離2の非隣接）16升 = 24升すべて通常手
        // 中央(5,5)からの距離2以内=24升すべて、ただし通常手（1回移動）は8升、タイプBは16升
        var 隣接1回 = 通常手.Where(手 =>
            Math.Abs(手.Get移動先.列 - 5) <= 1 &&
            Math.Abs(手.Get移動先.段 - 5) <= 1).ToList();
        Assert.Equal(8, 隣接1回.Count);
    }

    [Fact]
    public void 獅王_タイプA2回移動が生成される()
    {
        var 盤面 = new C盤面("k8/9/9/9/4+K4/9/9/9/9 b - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        var 二回手 = 手一覧.Where(手 => 手.Is獅王2回移動).ToList();
        Assert.True(二回手.Count > 0);
        // 中間升は常に隣接升
        Assert.All(二回手, 手 =>
        {
            Assert.True(Math.Abs(手.Get中間.列 - 5) <= 1);
            Assert.True(Math.Abs(手.Get中間.段 - 5) <= 1);
        });
    }

    [Fact]
    public void 獅王_元の升に戻る2回移動が生成される()
    {
        var 盤面 = new C盤面("k8/9/9/9/4+K4/9/9/9/9 b - 1");
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // 元に戻る手（移動元 == 移動先）が生成される
        var 戻る手 = 手一覧.Where(手 =>
            手.Is獅王2回移動 &&
            手.Get移動先.Equals(Sq(5, 5))).ToList();
        Assert.True(戻る手.Count > 0, "元に戻る獅王2回移動が生成されていない");
    }

    [Fact]
    public void 獅王_隣接に自駒があれば通過不可()
    {
        var 盤面 = new C盤面("k8/9/9/9/4+K4/9/9/9/9 b - 1");
        盤面.Set駒(5, 4, E駒種.歩兵, E手番.先手); // 前に自駒
        var 手一覧 = Get移動手(盤面, Sq(5, 5));

        // (5,4)への1回移動は不可
        Assert.DoesNotContain(手一覧, 手 =>
            !手.Is獅王2回移動 && 手.Get移動先.Equals(Sq(5, 4)));
        // (5,4)を中間として通過する2回移動も不可
        Assert.DoesNotContain(手一覧, 手 =>
            手.Is獅王2回移動 && 手.Get中間.Equals(Sq(5, 4)));
    }
}
