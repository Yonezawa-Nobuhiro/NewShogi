using System.Diagnostics;
using 変成将棋.AI;
using 変成将棋.Models;

public static class Benchmark
{
    public static void Run(string paramsPath, bool nnue = true)
    {
        Console.WriteLine($"=== αβ 速度ベンチマーク (NNUE:{nnue}) ===");

        using var ai = new CαβAI(paramsPath, "NOBOOK", nnueEnabled: nnue);
        var board = new C盤面();
        board.Reset();

        // 20手平均計測（10手ごとに盤面リセット）
        int moves = 0;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 20; i++)
        {
            var 手 = ai.Get手(board);
            if (手 == null) break;
            board.Apply(手.Value);
            moves++;
            if (moves % 10 == 0) board.Reset();
        }

        sw.Stop();
        double msPerMove = sw.Elapsed.TotalMilliseconds / moves;
        double movesPerSec = 1000.0 / msPerMove;

        Console.WriteLine($"  手数: {moves}  合計: {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  1手あたり: {msPerMove:F1} ms  ({movesPerSec:F0} 手/秒)");
        ai.PrintNNUEStat();
    }
}
