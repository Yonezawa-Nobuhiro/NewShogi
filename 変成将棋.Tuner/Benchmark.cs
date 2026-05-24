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

        // 1局通しで計測（手詰まりは新局面にリセットして継続）
        const int 目標手数 = 200;
        int moves = 0;
        var sw = Stopwatch.StartNew();

        while (moves < 目標手数)
        {
            var 手 = ai.Get手(board);
            if (手 == null)
            {
                board.Reset();
                continue;
            }
            board.Apply(手.Value);
            moves++;
        }

        sw.Stop();
        double msPerMove = sw.Elapsed.TotalMilliseconds / moves;
        double movesPerSec = 1000.0 / msPerMove;

        Console.WriteLine($"  手数: {moves}  合計: {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.WriteLine($"  1手あたり: {msPerMove:F1} ms  ({movesPerSec:F0} 手/秒)");
        ai.PrintNNUEStat();
    }
}
