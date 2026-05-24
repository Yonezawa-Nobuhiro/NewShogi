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

        int moves = 0;
        var sw = Stopwatch.StartNew();

        while (true)
        {
            var 手 = ai.Get手(board);
            if (手 == null) break;          // 詰み → 終局
            board.Apply(手.Value);
            moves++;
            if (moves >= 300) break;        // 最大手数ガード
        }

        sw.Stop();
        double msPerMove = moves > 0 ? sw.Elapsed.TotalMilliseconds / moves : 0;
        double movesPerSec = msPerMove > 0 ? 1000.0 / msPerMove : 0;

        Console.WriteLine($"  手数: {moves}  合計: {sw.Elapsed.TotalSeconds:F1} 秒");
        Console.WriteLine($"  1手あたり: {msPerMove:F1} ms  ({movesPerSec:F0} 手/秒)");
        ai.PrintNNUEStat();
    }
}
