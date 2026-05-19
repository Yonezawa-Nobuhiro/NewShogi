using System.Text;
using 変成将棋.AI;
using 変成将棋.Models;

/// <summary>
/// αβ自己対局を行い、各局面の静的評価値を (SFEN\tscore) 形式で出力する。
/// NNUE 学習データ生成用。
/// </summary>
public static class EvalDataGen
{
    public static void Run(string paramsPath, int numGames, string outPath, int seed)
    {
        var p = αβパラメータ.Load(paramsPath);
        int[] 駒価値 = BuildPieceValues(p);
        var rng = new Random(seed);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        using var w = new StreamWriter(outPath, false, Encoding.UTF8);

        int total = 0;
        for (int g = 0; g < numGames; g++)
        {
            using var ai先手 = new CαβAI(paramsPath, "NOBOOK");
            using var ai後手 = new CαβAI(paramsPath, "NOBOOK");
            var board = new C盤面();
            board.Reset();

            for (int m = 0; m < 400; m++)
            {
                // ランダム序盤はスキップ（評価値が不安定なため）
                if (m >= 8)
                {
                    int score = C評価関数.Evaluate(board, p, 駒価値);
                    w.WriteLine($"{board.ToSFEN()}\t{score}");
                    total++;
                }

                S手? 手;
                if (m < 8)
                {
                    S手[] buf = new S手[600];
                    int cnt = C合法手生成器.Get合法手(board, buf);
                    if (cnt == 0) break;
                    手 = buf[rng.Next(cnt)];
                }
                else
                {
                    var current = board.手番 == E手番.先手 ? ai先手 : ai後手;
                    手 = current.Get手(board);
                    if (手 == null) break;
                }
                board.Apply(手.Value);
            }

            if ((g + 1) % 100 == 0)
                Console.Error.Write($"\r  [{g + 1}/{numGames}]  {total:N0} サンプル");
        }
        Console.Error.WriteLine($"\n完了: {total:N0} サンプル → {outPath}");
    }

    private static int[] BuildPieceValues(αβパラメータ p)
    {
        var v = p.駒価値;
        var arr = new int[17];
        arr[(int)E駒種.歩兵] = v.歩兵;
        arr[(int)E駒種.香車] = v.香車;
        arr[(int)E駒種.桂馬] = v.桂馬;
        arr[(int)E駒種.銀将] = v.銀将;
        arr[(int)E駒種.金将] = v.金将;
        arr[(int)E駒種.角行] = v.角行;
        arr[(int)E駒種.飛車] = v.飛車;
        arr[(int)E駒種.と金] = v.と金;
        arr[(int)E駒種.竪行] = v.竪行;
        arr[(int)E駒種.騎兵] = v.騎兵;
        arr[(int)E駒種.麒麟] = v.麒麟;
        arr[(int)E駒種.鳳凰] = v.鳳凰;
        arr[(int)E駒種.龍馬] = v.龍馬;
        arr[(int)E駒種.龍王] = v.龍王;
        arr[(int)E駒種.獅王] = v.獅王;
        return arr;
    }
}
