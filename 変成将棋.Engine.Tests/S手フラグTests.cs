using 変成将棋.Models;

namespace 変成将棋.Engine.Tests;

/// <summary>S手の各フラグ・プロパティの単体テスト。</summary>
public class S手フラグTests
{
    private static S升座標 Sq(int 列, int 段) => new((byte)列, (byte)段);

    // ─── 通常手 ─────────────────────────────────────────────────────────

    [Fact]
    public void 通常手_フラグが正しい()
    {
        var 手 = S手.Create通常(Sq(5, 5), Sq(5, 4));
        Assert.False(手.Is打ち);
        Assert.False(手.Is成り);
        Assert.False(手.Is獅王2回移動);
        Assert.Equal(Sq(5, 5), 手.Get移動元);
        Assert.Equal(Sq(5, 4), 手.Get移動先);
    }

    [Fact]
    public void 成り手_フラグが正しい()
    {
        var 手 = S手.Create通常(Sq(5, 4), Sq(5, 3), 成り: true);
        Assert.False(手.Is打ち);
        Assert.True(手.Is成り);
        Assert.False(手.Is獅王2回移動);
        Assert.Equal(Sq(5, 4), 手.Get移動元);
        Assert.Equal(Sq(5, 3), 手.Get移動先);
    }

    [Fact]
    public void 不成り手_Is成りがfalse()
    {
        var 手 = S手.Create通常(Sq(3, 4), Sq(3, 3), 成り: false);
        Assert.False(手.Is成り);
    }

    // ─── 打ち手 ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(E駒種.歩兵)]
    [InlineData(E駒種.香車)]
    [InlineData(E駒種.桂馬)]
    [InlineData(E駒種.銀将)]
    [InlineData(E駒種.金将)]
    [InlineData(E駒種.角行)]
    [InlineData(E駒種.飛車)]
    public void 打ち手_駒種ごとのフラグが正しい(E駒種 駒種)
    {
        var 手 = S手.Create打ち(駒種, Sq(5, 5));
        Assert.True(手.Is打ち);
        Assert.False(手.Is成り);
        Assert.False(手.Is獅王2回移動);
        Assert.Equal(駒種, 手.Get打ち駒);
        Assert.Equal(Sq(5, 5), 手.Get移動先);
    }

    [Fact]
    public void 打ち手_移動元は無効座標()
    {
        var 手 = S手.Create打ち(E駒種.金将, Sq(3, 7));
        Assert.False(手.Get移動元.Is有効); // 移動元 = 0x00 = 無効
    }

    // ─── 獅王2回移動 ────────────────────────────────────────────────────

    [Fact]
    public void 獅王2回移動_フラグが正しい()
    {
        var 手 = S手.Create獅王2回移動(Sq(5, 5), Sq(5, 4), Sq(5, 3));
        Assert.False(手.Is打ち);
        Assert.False(手.Is成り);
        Assert.True(手.Is獅王2回移動);
        Assert.Equal(Sq(5, 5), 手.Get移動元);
        Assert.Equal(Sq(5, 4), 手.Get中間);
        Assert.Equal(Sq(5, 3), 手.Get移動先);
    }

    [Fact]
    public void 獅王2回移動_元に戻る場合も移動先が正しい()
    {
        // 獅王が中間升の駒を取って元の升に戻るパス
        var 手 = S手.Create獅王2回移動(Sq(5, 5), Sq(5, 4), Sq(5, 5));
        Assert.True(手.Is獅王2回移動);
        Assert.Equal(Sq(5, 5), 手.Get移動元);
        Assert.Equal(Sq(5, 4), 手.Get中間);
        Assert.Equal(Sq(5, 5), 手.Get移動先); // 元に戻る
    }

    // ─── 通常手（1回移動）との区別 ──────────────────────────────────────

    [Fact]
    public void 通常手_Is獅王2回移動がfalse()
    {
        // 通常手は中間 = 0 なので Is獅王2回移動 = false
        var 手 = S手.Create通常(Sq(5, 5), Sq(5, 3)); // 距離2のジャンプ（タイプB相当）
        Assert.False(手.Is獅王2回移動);
    }
}
