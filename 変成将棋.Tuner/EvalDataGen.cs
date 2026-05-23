using System.Text;
using 変成将棋.AI;
using 変成将棋.Models;

/// <summary>
/// αβ自己対局を行い、各局面の αβ 探索評価値を (SFEN\tscore) 形式で出力する。
/// NNUE 学習データ生成用。全ゲームを並列実行する。
/// </summary>
public static class EvalDataGen
{
    public static void Run(string paramsPath, int numGames, string outPath, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

        // ゲームごとに結果を収集（並列安全）
        var gameLines = new List<string>[numGames];
        int completed  = 0;

        Parallel.For(0, numGames, g =>
        {
            using var ai先手 = new CαβAI(paramsPath, "NOBOOK");
            using var ai後手 = new CαβAI(paramsPath, "NOBOOK");
            var board = new C盤面();
            board.Reset();
            var rng = new Random(seed + g);
            var lines = new List<string>(400);

            for (int m = 0; m < 400; m++)
            {
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
                    var (bestMove, score) = current.Get手とスコア(board);
                    if (bestMove == null) break;
                    手 = bestMove;
                    lines.Add($"{board.ToSFEN()}\t{score}");
                }
                board.Apply(手.Value);
            }

            gameLines[g] = lines;
            int done = Interlocked.Increment(ref completed);
            if (done % Math.Max(1, numGames / 20) == 0 || done == numGames)
                Console.Error.Write($"\r  [{done}/{numGames}]  {gameLines[..done].Where(l => l != null).Sum(l => l.Count):N0} サンプル");
        });

        Console.Error.WriteLine();

        // 順番通りに書き出し
        int total = 0;
        using var w = new StreamWriter(outPath, false, Encoding.UTF8);
        foreach (var lines in gameLines)
        {
            if (lines == null) continue;
            foreach (var line in lines) w.WriteLine(line);
            total += lines.Count;
        }

        Console.Error.WriteLine($"完了: {total:N0} サンプル → {outPath}");
    }
}
