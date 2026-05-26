using System.Diagnostics;
using System.Text;
using 変成将棋.AI;
using 変成将棋.AI.αβAI;
using 変成将棋.Models;

/// <summary>
/// αβ自己対局を行い、各局面の αβ 探索評価値を (SFEN\tscore) 形式で出力する。
/// NNUE 学習データ生成用。全ゲームを並列実行する。
/// </summary>
public static class EvalDataGen
{
    static string FmtTime(double sec)
    {
        int s = (int)sec;
        int h = s / 3600, m = (s % 3600) / 60, ss = s % 60;
        return h > 0 ? $"{h}:{m:D2}:{ss:D2}" : $"{m}:{ss:D2}";
    }

    static string Bar(int done, int total, int width = 28)
    {
        double ratio = total > 0 ? Math.Min((double)done / total, 1.0) : 0;
        int filled = (int)(width * ratio);
        string arrow = filled < width ? ">" : "";
        string dashes = new('-', Math.Max(0, width - filled - arrow.Length));
        return $"[{new string('=', filled)}{arrow}{dashes}]";
    }

    public static void Run(string paramsPath, int numGames, string outPath, int seed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");

        Console.WriteLine($"  自己対局データ生成  {numGames} 局  → {outPath}");
        Console.WriteLine("  ─────────────────────────────────────────────────────────────────────");

        var gameLines = new List<string>[numGames];
        int completed = 0;
        long sampleCount = 0;
        var sw = Stopwatch.StartNew();

        var printLock = new object();
        // αβ探索は stackalloc を多用するため、スレッドプールの小スタックでは
        // StackOverflow が発生する。専用スレッド（64MB）でゲームを実行する。
        const int StackSize = 64 * 1024 * 1024;
        var semaphore = new SemaphoreSlim(6);
        var tasks = new Task[numGames];

        for (int g = 0; g < numGames; g++)
        {
            int gLocal = g;
            tasks[g] = Task.Run(async () =>
            {
                await semaphore.WaitAsync();
                List<string> lines = new(400);
                Exception? err = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        using var ai先手 = new CαβAI(paramsPath, "NOBOOK");
                        using var ai後手 = new CαβAI(paramsPath, "NOBOOK");
                        var board = new C盤面();
                        board.Reset();
                        var rng = new Random(seed + gLocal);

                        for (int m = 0; m < 300; m++)
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
                    }
                    catch (Exception ex) { err = ex; }
                }, StackSize);

                thread.Start();
                thread.Join();
                semaphore.Release();

                if (err != null)
                    Console.WriteLine($"\n  ゲーム{gLocal} エラー: {err.GetType().Name}: {err.Message}");

                gameLines[gLocal] = lines;
                int done = Interlocked.Increment(ref completed);
                long totalSamples = Interlocked.Add(ref sampleCount, lines.Count);

                double elapsed = sw.Elapsed.TotalSeconds;
                double eta = done > 0 ? elapsed / done * (numGames - done) : 0;
                lock (printLock)
                {
                    Console.Write(
                        $"\r  {Bar(done, numGames)}  {done}/{numGames}  {(double)done/numGames*100:F0}%  " +
                        $"eta {FmtTime(eta)}  {totalSamples:N0} サンプル  経過 {FmtTime(elapsed)}");
                }
            });
        }

        Task.WaitAll(tasks);

        Console.WriteLine();

        int total = 0;
        using var w = new StreamWriter(outPath, false, Encoding.UTF8);
        foreach (var lines in gameLines)
        {
            if (lines == null) continue;
            foreach (var line in lines) w.WriteLine(line);
            total += lines.Count;
        }

        Console.WriteLine($"\n  完了  合計 {FmtTime(sw.Elapsed.TotalSeconds)}  {total:N0} サンプル → {outPath}");
    }
}
