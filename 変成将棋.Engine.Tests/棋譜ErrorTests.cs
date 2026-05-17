using 変成将棋.Models;

namespace 変成将棋.Engine.Tests;

/// <summary>
/// ml/checkpoints/棋譜error.kf の再現テスト。
///
/// このファイルは空き王手放置バグ（XOR-AND利きビット更新の欠陥）によって生じた
/// 不正な棋譜を記録している。
///
/// 各局面について:
///   1. Get合法手が王手放置を含まないことを検証する。
///   2. 棋譜の次局面に到達できる手が合法手に含まれているか確認し、
///      含まれていない場合はその手が王手放置であることを証明する（エラー箇所の特定）。
/// </summary>
public class 棋譜ErrorTests
{
    // 棋譜error.kf から抽出した全局面 SFEN（初期局面 + 32手分 = 33局面）
    private static readonly string[] 棋譜局面 =
    [
        // 手 0（初期局面）
        "lnsgkgsnl/1r5b1/ppppppppp/9/9/9/PPPPPPPPP/1B5R1/LNSGKGSNL b - 1",
        // 手 1
        "lnsgkgsnl/1r5b1/ppppppppp/9/9/7P1/PPPPPPP1P/1B5R1/LNSGKGSNL w - 1",
        // 手 2
        "lnsgkg1nl/1r4sb1/ppppppppp/9/9/7P1/PPPPPPP1P/1B5R1/LNSGKGSNL b - 1",
        // 手 3
        "lnsgkg1nl/1r4sb1/ppppppppp/9/7P1/9/PPPPPPP1P/1B5R1/LNSGKGSNL w - 1",
        // 手 4
        "lnsgk2nl/1r3gsb1/ppppppppp/9/7P1/9/PPPPPPP1P/1B5R1/LNSGKGSNL b - 1",
        // 手 5
        "lnsgk2nl/1r3gsb1/ppppppppp/7P1/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL w - 1",
        // 手 6
        "lnsgk2nl/1r3gsb1/ppp1ppppp/3p3P1/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL b - 1",
        // 手 7（先手歩が成る）
        "lnsgk2nl/1r3gsb1/ppp1ppp+Pp/3p5/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL w P 1",
        // 手 8
        "lns1k2nl/1rg2gsb1/ppp1ppp+Pp/3p5/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL b P 1",
        // 手 9（先手歩打ち→成る）
        "lns1k2nl/1rg2gs+P1/ppp1ppp1p/3p5/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL w BP 1",
        // 手10
        "lns4nl/1rgk1gs+P1/ppp1ppp1p/3p5/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL b BP 1",
        // 手11
        "lns4n+P/1rgk1gs2/ppp1ppp1p/3p5/9/9/PPPPPPP1P/1B5R1/LNSGKGSNL w BLP 1",
        // 手12
        "lns4n+P/1rgk1gs2/ppp1ppp1p/9/3p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL b BLP 1",
        // 手13
        "lns4+P1/1rgk1gs2/ppp1ppp1p/9/3p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL w BNLP 1",
        // 手14
        "lns4+P1/1rgk1gs2/p1p1ppp1p/1p7/3p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL b BNLP 1",
        // 手15
        "lns6/1rgk1gs+P1/p1p1ppp1p/1p7/3p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL w BNLP 1",
        // 手16
        "lns6/1rgk1gs+P1/2p1ppp1p/pp7/3p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL b BNLP 1",
        // 手17
        "lns6/1rgk1g+P2/2p1ppp1p/pp7/3p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL w BSNLP 1",
        // 手18
        "lns6/1rgk1g+P2/2p1ppp1p/1p7/p2p5/9/PPPPPPP1P/1B5R1/LNSGKGSNL b BSNLP 1",
        // 手19（先手飛車→龍王）
        "lns6/1rgk1g+P+R1/2p1ppp1p/1p7/p2p5/9/PPPPPPP1P/1B7/LNSGKGSNL w BSNLP 1",
        // 手20
        "1ns6/lrgk1g+P+R1/2p1ppp1p/1p7/p2p5/9/PPPPPPP1P/1B7/LNSGKGSNL b BSNLP 1",
        // 手21
        "1ns6/lrgk1g+P+R1/2p1ppp1p/1p7/p2p5/9/PPPPPPP1P/1B3S3/LNSGKG1NL w BSNLP 1",
        // 手22
        "1ns6/lrgk1g+P+R1/2p1p1p1p/1p3p3/p2p5/9/PPPPPPP1P/1B3S3/LNSGKG1NL b BSNLP 1",
        // 手23
        "1ns6/lrgk1g+P+R1/2p1p1p1p/1p3p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL w BSNLP 1",
        // 手24
        "1ns6/lrgk2+P+R1/2p1pgp1p/1p3p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL b BSNLP 1",
        // 手25
        "1ns6/lrgk1+P1+R1/2p1pgp1p/1p3p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL w BSNLP 1",
        // 手26
        "1ns6/1rgk1+P1+R1/l1p1pgp1p/1p3p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL b BSNLP 1",
        // 手27
        "1ns6/1rgk1+P1+R1/l1p1pgp1p/1p1L1p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL w BSNP 1",
        // 手28
        "1ns6/1r1k1+P1+R1/l1pgpgp1p/1p1L1p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL b BSNP 1",
        // 手29
        "1ns6/1r1k3+R1/l1pgp+Pp1p/1p1L1p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL w BGSNP 1",
        // 手30（エラー手直前局面: 後手手番）
        "1ns6/1r1k3+R1/l1p1p+Pp1p/1p1g1p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL b BGSNPl 1",
        // 手31（エラー手後局面: 先手龍王が後手玉を取る → 不正）
        "1ns6/1r1+R5/l1p1p+Pp1p/1p1g1p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL w BGSNPl 1",
        // 手32（手31と同一: 棋譜の重複記録）
        "1ns6/1r1+R5/l1p1p+Pp1p/1p1g1p3/p2p5/5P3/PPPPP1P1P/1B3S3/LNSGKG1NL w BGSNPl 1",
    ];

    // ─── テスト 1：全局面で合法手に王手放置なし ─────────────────────────

    [Fact]
    public void 棋譜全局面で合法手に王手放置なし()
    {
        // 棋譜に登場する全33局面について、Get合法手が王手放置を含まないことを確認する。
        // 局面自体が合法かどうかに関わらず、合法手生成の正確性を検証する。
        var 不正局面 = new List<(int 手番号, string sfen, string 詳細)>();
        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];

        for (int i = 0; i < 棋譜局面.Length; i++)
        {
            var sfen = 棋譜局面[i];
            var 盤面 = new C盤面(sfen);
            var 手番 = 盤面.手番;

            int 手数 = C合法手生成器.Get合法手(盤面, バッファ);

            for (int j = 0; j < 手数; j++)
            {
                var 手 = バッファ[j];
                var 取消 = 盤面.Apply(手);
                var (先手利き, 後手利き) = C利き管理.Compute全利き(盤面);
                var 相手利き = 手番 == E手番.先手 ? 後手利き : 先手利き;
                var 玉 = 盤面.Find玉(手番);

                if (玉.Is有効 && 相手利き.Contains(玉))
                    不正局面.Add((i, sfen, $"手{j}で王手放置"));

                盤面.Undo(手, 取消);
            }
        }

        Assert.True(不正局面.Count == 0,
            $"王手放置を含む局面が {不正局面.Count} 件:\n"
            + string.Join("\n", 不正局面.Select(x => $"  手番{x.手番号}: {x.詳細}")));
    }

    // ─── テスト 2：棋譜の各遷移を検証 ──────────────────────────────────

    [Fact]
    public void 棋譜の合法遷移は全て合法手に含まれる()
    {
        // 連続する局面ペアについて、前局面の合法手から次局面に到達できることを確認する。
        // 到達できない遷移は「不正な手（王手放置）が指された箇所」であり、
        // その手が擬似合法手に存在し、かつ王手放置であることを検証する。
        int エラー手数 = 0;
        var エラー詳細 = new List<string>();
        Span<S手> 合法バッファ = stackalloc S手[C合法手生成器.最大手数];
        Span<S手> 擬似バッファ = stackalloc S手[C合法手生成器.最大手数];

        for (int i = 0; i < 棋譜局面.Length - 1; i++)
        {
            var sfen前 = 棋譜局面[i];
            var sfen後 = 棋譜局面[i + 1];

            // 連続する局面が同一なら遷移なし（棋譜末尾の重複など）
            if (sfen前 == sfen後) continue;

            var 盤面 = new C盤面(sfen前);
            var 手番 = 盤面.手番;

            // 合法手から次局面に到達できるか確認
            bool 合法到達 = false;
            int 合法手数 = C合法手生成器.Get合法手(盤面, 合法バッファ);

            for (int j = 0; j < 合法手数; j++)
            {
                var 取消 = 盤面.Apply(合法バッファ[j]);
                if (盤面.ToSFEN() == sfen後) 合法到達 = true;
                盤面.Undo(合法バッファ[j], 取消);
                if (合法到達) break;
            }

            if (合法到達) continue;

            // ── 到達できない遷移 → エラー手の特定 ──────────────────────
            エラー手数++;

            // 擬似合法手（王手放置チェックなし）から次局面に到達する手を探す
            int 擬似手数 = C合法手生成器.Generate擬似合法手(盤面, 擬似バッファ);

            S手? 誤手 = null;
            for (int j = 0; j < 擬似手数; j++)
            {
                var 取消 = 盤面.Apply(擬似バッファ[j]);
                if (盤面.ToSFEN() == sfen後) 誤手 = 擬似バッファ[j];
                盤面.Undo(擬似バッファ[j], 取消);
                if (誤手.HasValue) break;
            }

            // 誤手が見つかった → それが王手放置であることを確認
            if (誤手.HasValue)
            {
                var 取消 = 盤面.Apply(誤手.Value);
                var (先手利き, 後手利き) = C利き管理.Compute全利き(盤面);
                var 相手利き = 手番 == E手番.先手 ? 後手利き : 先手利き;
                var 玉 = 盤面.Find玉(手番);
                bool 王手放置 = !玉.Is有効 || 相手利き.Contains(玉);
                盤面.Undo(誤手.Value, 取消);

                Assert.True(王手放置,
                    $"手{i}→{i + 1}: 合法手に含まれない遷移が王手放置でない（棋譜自体が不正）");

                エラー詳細.Add($"手{i + 1}: 空き王手放置が正しく除外された（旧バグで許可されていた手）");
            }
            else
            {
                // 擬似合法手にも存在しない → 棋譜が根本的に不正
                エラー詳細.Add($"手{i + 1}: 次局面に到達できる手が擬似合法手にも存在しない");
                Assert.Fail($"手{i}→{i + 1}: 棋譜の遷移が不正（擬似合法手にも存在しない）\n前: {sfen前}\n後: {sfen後}");
            }
        }

        // エラー手の存在はバグの証拠として記録するが、
        // 「王手放置として正しく除外された」なら現在の実装は正しい
        if (エラー手数 > 0)
        {
            // エラー手が全て王手放置として除外されているなら合格
            // （上の Assert.True で確認済み）
        }
    }

    // ─── テスト 3：ゲーム終了手（後手玉取り）が合法手として認識される ─

    [Fact]
    public void 手30の局面で先手龍王が後手玉を取る手は合法()
    {
        // 手30局面（先手手番）から先手龍王が後手玉を取ってゲームが終わる。
        // この手が合法手リストに含まれることで「玉取り = 正規のゲーム終了」を確認する。
        // （手31は後手玉が消えた後の局面で、後手の合法手=0 = ゲームオーバー）
        var 盤面 = new C盤面(棋譜局面[30]); // 先手手番
        Assert.Equal(E手番.先手, 盤面.手番);

        var sfen次局面 = 棋譜局面[31]; // 先手龍王が後手玉を取った後の局面

        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];
        int 手数 = C合法手生成器.Get合法手(盤面, バッファ);

        bool 合法到達 = false;
        for (int i = 0; i < 手数; i++)
        {
            var 取消 = 盤面.Apply(バッファ[i]);
            if (盤面.ToSFEN() == sfen次局面) 合法到達 = true;
            盤面.Undo(バッファ[i], 取消);
            if (合法到達) break;
        }

        Assert.True(合法到達, "先手龍王が後手玉を取る手が合法手に含まれていない");

        // 取られた後の局面（手31）では後手玉が存在しないため後手の合法手は0
        var 終局盤面 = new C盤面(sfen次局面);
        Assert.Equal(E手番.後手, 終局盤面.手番);
        Assert.False(終局盤面.Find玉(E手番.後手).Is有効, "後手玉がまだ存在している（不正）");
    }

    // ─── テスト 4：ゲーム途中局面では合法手が 0 にならない ─────────────

    [Fact]
    public void 棋譜途中局面の合法手数は0より多い()
    {
        // 両玉が存在する局面（ゲーム継続中）では合法手が1以上あることを確認する。
        // 手31以降は後手玉が取られたゲーム終了局面のため除外する。
        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];
        for (int i = 0; i < 棋譜局面.Length; i++)
        {
            var 盤面 = new C盤面(棋譜局面[i]);

            // 両玉が存在しない（ゲーム終了済）局面はスキップ
            bool 先手玉あり = 盤面.Find玉(E手番.先手).Is有効;
            bool 後手玉あり = 盤面.Find玉(E手番.後手).Is有効;
            if (!先手玉あり || !後手玉あり) continue;

            int 手数 = C合法手生成器.Get合法手(盤面, バッファ);

            Assert.True(手数 > 0, $"局面{i}で合法手が0（{棋譜局面[i]}）");
        }
    }
}
