using System.Diagnostics;
using 変成将棋.AI;
using 変成将棋.AI.αβAI;
using 変成将棋.Models;

public static class Benchmark
{
    public static void Run(string paramsPath, bool nnue = true, string? kifPath = null, int maxMoves = 250)
    {
        var p = αβパラメータ.Load(paramsPath);
        Console.WriteLine($"=== αβ 速度ベンチマーク (NNUE:{nnue}, 深さ:{p.探索深さ}, 時間:{p.思考時間ms}ms) ===");

        using var ai = new CαβAI(paramsPath, "NOBOOK", nnueEnabled: nnue);
        var board = new C盤面();
        board.Reset();

        var sfens = new List<string> { board.ToSFEN() };
        int moves = 0;
        long accumNodes = 0, accumQNodes = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            Console.Error.WriteLine($"  [{moves + 1}手目開始] {board.ToSFEN()}");
            var moveSw = Stopwatch.StartNew();
            var 手 = ai.Get手(board);
            moveSw.Stop();
            accumNodes  += ai.StatNodes;
            accumQNodes += ai.StatQNodes;
            if (手 == null) break;          // 詰み → 終局
            board.Apply(手.Value);
            moves++;
            sfens.Add(board.ToSFEN());
            Console.Error.Write($"\r  {moves}手目: {moveSw.Elapsed.TotalMilliseconds:F0} ms   ");
            // 相手玉を取っていたら終局（変成将棋では玉直取りが合法）
            if (!board.Find玉(board.手番).Is盤内) break;
            if (moves >= maxMoves) break;    // 最大手数ガード
        }
        Console.Error.WriteLine();

        sw.Stop();
        double msPerMove = moves > 0 ? sw.Elapsed.TotalMilliseconds / moves : 0;
        double movesPerSec = msPerMove > 0 ? 1000.0 / msPerMove : 0;

        long totalNodes = accumNodes + accumQNodes;
        double nps = sw.Elapsed.TotalSeconds > 0 ? totalNodes / sw.Elapsed.TotalSeconds : 0;

        Console.WriteLine($"  手数: {moves}  合計: {sw.Elapsed.TotalSeconds:F1} 秒");
        Console.WriteLine($"  1手あたり: {msPerMove:F1} ms  ({movesPerSec:F0} 手/秒)");
        Console.WriteLine($"  nodes={accumNodes:N0}  qnodes={accumQNodes:N0}  合計={totalNodes:N0}");
        Console.WriteLine($"  NPS: {nps:N0} 局面/秒");
        ai.PrintNNUEStat();

        if (kifPath != null)
        {
            using var w = new StreamWriter(kifPath, false, System.Text.Encoding.UTF8);
            w.WriteLine("# 変成将棋棋譜");
            w.WriteLine($"# 日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            w.WriteLine($"# 手数: {moves}");
            w.WriteLine($"# 深さ: {p.探索深さ}  NNUE: {nnue}");
            w.WriteLine();
            foreach (var s in sfens) w.WriteLine(s);
            Console.WriteLine($"  棋譜保存: {kifPath}");
        }

        RunTsume(p.詰み探索手数);
    }

    // 詰将棋ベンチマーク: 代表局面で C詰将棋探索 の速度を計測
    private static void RunTsume(int 最大手数)
    {
        // 変成将棋の終盤局面サンプル（詰みがあるものもないものも混在）
        var 局面リスト = new (string ラベル, string SFEN)[]
        {
            ("初期局面",   "lnsgkgsnl/1r5b1/ppppppppp/9/9/9/PPPPPPPPP/1B5R1/LNSGKGSNL b - 1"),
            ("終盤A(先手)", "l2g5/3kgb3/ns7/4p3p/p1p4N1/1p5P1/P1N2P2P/L1S3S1L/4KG3 b RSNL4Prbg4p 1"),
            ("終盤B(後手)", "l2gk4/4gb3/n8/2s1p4/7Np/Pp5P1/2N2P2P/L1S3S1L/4KG3 w RSNL4Prbg5p 1"),
            ("終盤C(先手)", "l2gk4/4gb3/n8/2s1p4/7Np/Pp5P1/3+p1P2P/L2s2S1L/3K1G3 b RSNL5Prbgn5p 1"),
        };

        Console.WriteLine();
        Console.WriteLine($"=== 詰将棋探索ベンチマーク (最大{最大手数}手) ===");

        long totalTNodes = 0;
        double totalTMs  = 0;

        foreach (var (ラベル, sfen) in 局面リスト)
        {
            var board = new C盤面(sfen);
            var tsw = Stopwatch.StartNew();
            var result = C詰将棋探索.詰み探索(board, 最大手数);
            tsw.Stop();

            long n = C詰将棋探索.LastNodes;
            totalTNodes += n;
            totalTMs    += tsw.Elapsed.TotalMilliseconds;

            string 結果 = result.HasValue ? $"{result.Value.手数}手詰め" : "詰みなし";
            double knps = tsw.Elapsed.TotalSeconds > 0 ? n / tsw.Elapsed.TotalSeconds / 1000.0 : 0;
            Console.WriteLine($"  [{ラベル}]  {結果}  nodes={n:N0}  {tsw.Elapsed.TotalMilliseconds:F1}ms  ({knps:F0}k局面/秒)");
        }

        double avgKnps = totalTMs > 0 ? totalTNodes / (totalTMs / 1000.0) / 1000.0 : 0;
        Console.WriteLine($"  合計 nodes={totalTNodes:N0}  平均 NPS={avgKnps:F0}k局面/秒");
    }
}
