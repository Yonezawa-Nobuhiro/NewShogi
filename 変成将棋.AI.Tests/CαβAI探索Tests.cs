using 変成将棋.AI;
using 変成将棋.AI.αβAI;
using 変成将棋.Models;

namespace 変成将棋.Engine.Tests;

/// <summary>
/// CαβAI の探索ロジック回帰テスト。
/// NNUEなし（nnueEnabled=false）で実行するためファイル依存なし。
/// </summary>
public class CαβAI探索Tests
{
    // ── 玉取りクラッシュ修正（2026-05-25） ──────────────────────────────
    // 変成将棋では相手の玉を直接取れる手が合法手として存在する。
    // 修正前は Apply 後に Find玉 が S升座標.なし を返し、
    // NNUE の Update加算器 で NullReferenceException が発生していた。

    [Fact]
    public void 玉取り可能局面でAIがクラッシュしない()
    {
        // 棋譜_20260525_201703.kf の75手目局面（先手番、持ち駒多数）
        const string sfen = "1ksg3nl/7+R1/Npp1p3p/5p1p1/3p1Pg2/LBP6/NP1PP3P/1SGK1Rp2/5G1NL b B2SNL4P 1";
        var board = new C盤面(sfen);
        using var ai = new CαβAI(nnueEnabled: false);
        var 手 = ai.Get手(board);
        Assert.NotNull(手);
    }

    [Fact]
    public void 詰み手が選ばれる_33角成局面()
    {
        // 先手番: 33角(成)で後手玉が詰む局面
        const string sfen = "ln1gkgsnl/1r1s3b1/p1pp1pppp/9/1p2B4/2P6/PP1P1PPPP/4R4/LNSGKGSNL b Pp 1";
        var board = new C盤面(sfen);
        using var ai = new CαβAI(nnueEnabled: false);
        var 手 = ai.Get手(board);
        Assert.NotNull(手);
        // 詰み探索の初手と一致することを確認
        var 詰み = C詰将棋探索.詰み探索(board, 9);
        Assert.True(詰み.HasValue, "詰みが見つからない");
        Assert.Equal(詰み.Value.初手, 手!.Value);
    }

    [Fact]
    public void 千日手局面で探索が終了する()
    {
        // 初期局面から探索してもクラッシュや無限ループにならないことを確認
        var board = new C盤面();
        board.Reset();
        using var ai = new CαβAI(nnueEnabled: false);
        var 手 = ai.Get手(board);
        Assert.NotNull(手);
    }

    // ── Aspiration Window 無限ループ修正（2026-05-25） ───────────────────
    // 玉取り可能局面では SearchRoot が 詰点数 を返す。
    // s >= β かつ β = 詰点数 のとき β が増やせず無限ループになるバグを修正。
    // フルウィンドウ(α<=-詰点数 && β>=詰点数)でもfailする場合は結果を採用して抜ける。

    [Fact]
    public void 局面63手目_詰み確認()
    {
        // 63手目局面: 後手番で先手玉への詰みがあるか確認
        const string sfen = "2sg1g1n1/l1k2s3/r3p4/2pp5/1p1n2P1L/4P2p1/1K3PN2/4G1RS1/1N3G3 w 2BL4Psl6p 1";
        var board = new C盤面(sfen);
        var result = C詰将棋探索.詰み探索(board, 9);
        Assert.True(result.HasValue, "詰みなし");
        Assert.Equal(5, result!.Value.手数);

        // AIが詰み初手を選ぶことを確認
        using var ai = new CαβAI(nnueEnabled: false);
        var aiの手 = ai.Get手(board);
        Assert.NotNull(aiの手);
        Assert.Equal(result.Value.初手, aiの手.Value);
    }

    [Fact]
    public void 詰み逃し_51金打ちで1手詰み()
    {
        // 詰み逃し.kf 96手目後局面（先手番）: 51金打ちで後手玉41が詰む
        // 後手玉41の逃げ場: 31=後手銀, 32=後手金, 42=角24の利き, 51=金を打つ, 52=金の利き → なし
        const string sfen = "l1s2ks2/r5g2/pp7/5K1B1/Pn2P4/9/9/L8/3G5 b BGL7Prg2s2nl7p 1";
        var board = new C盤面(sfen);
        using var ai = new CαβAI(nnueEnabled: false);
        var 手 = ai.Get手(board);
        Assert.NotNull(手);
        // 51金打ち = 打ち手, 移動先=(5,1), 駒種=金将
        Assert.True(手!.Value.Is打ち, $"打ち手でない: {手}");
        var 先 = 手.Value.Get移動先;
        Assert.Equal(5, 先.列);
        Assert.Equal(1, 先.段);
    }

    // ── Aspiration Window TT汚染によるやねうら式実装確認（2026-05-27） ──────
    // lnsg3nl/1r3kgs1/p1pppp3/6pR1/1p6p/2P5P/PPNPPPP2/1SG2G3/L3K1SNL b BPbp 1
    // この局面で旧Aspiration Window実装（fail highリトライ時のTT汚染）により
    // 明らかに駒損な22飛成が選ばれていた。

    [Fact]
    public void AspirationWindow_22飛成を選ばない()
    {
        // NNUE必須: C駒得評価器は全局面0を返すため、NNUEなしでは手が正しく評価されない
        const string sfen = "lnsg3nl/1r3kgs1/p1pppp3/6pR1/1p6p/2P5P/PPNPPPP2/1SG2G3/L3K1SNL b BPbp 1";
        var board = new C盤面(sfen);
        using var ai = new CαβAI(nnueEnabled: true);
        var 手 = ai.Get手(board);
        Assert.NotNull(手);
        // 22飛成（駒損の悪手）が選ばれないことを確認
        bool is22飛成 = 手!.Value.Is成り
                     && 手.Value.Get移動先.列 == 2
                     && 手.Value.Get移動先.段 == 2;
        Assert.False(is22飛成, $"22飛成が選ばれた: {手}");
    }

    [Fact]
    public async System.Threading.Tasks.Task AspirationWindow_詰みスコアで無限ループしない()
    {
        // 深さ3以上でAspiration Windowが使われる。
        // 玉取り1手詰め局面ではs=詰点数となりβを超え続けるため、修正前は無限ループ。
        const string sfen = "1ksg3nl/7+R1/Npp1p3p/5p1p1/3p1Pg2/LBP6/NP1PP3P/1SGK1Rp2/5G1NL b B2SNL4P 1";
        var board = new C盤面(sfen);
        using var ai = new CαβAI(nnueEnabled: false);
        using var cts = new System.Threading.CancellationTokenSource(10_000);
        var 手 = await System.Threading.Tasks.Task.Run(() => ai.Get手(board), cts.Token);
        Assert.NotNull(手);
    }
}
