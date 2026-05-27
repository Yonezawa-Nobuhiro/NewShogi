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

        var gameLines   = new List<string>[numGames];
        var gameMoves   = new int[numGames];    // 各局の手数
        var gameResults = new int[numGames];    // 1=先手勝ち  -1=後手勝ち  0=引き分け
        int completed = 0;
        long sampleCount = 0;
        int 先手勝ち = 0, 後手勝ち = 0, 引き分け = 0;
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
                int moves = 0;
                int result = 0;
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
                                if (cnt == 0)
                                {
                                    result = board.手番 == E手番.先手 ? -1 : 1;
                                    break;
                                }
                                手 = buf[rng.Next(cnt)];
                            }
                            else
                            {
                                var current = board.手番 == E手番.先手 ? ai先手 : ai後手;
                                var (bestMove, score) = current.Get手とスコア(board);
                                if (bestMove == null)
                                {
                                    result = board.手番 == E手番.先手 ? -1 : 1;
                                    break;
                                }
                                手 = bestMove;
                                lines.Add($"{board.ToSFEN()}\t{score}");
                            }
                            board.Apply(手.Value);
                            moves++;
                            if (!board.Find玉(board.手番).Is盤内)
                            {
                                result = board.手番 == E手番.先手 ? -1 : 1;
                                break;
                            }
                        }
                    }
                    catch (Exception ex) { err = ex; }
                }, StackSize);

                thread.Start();
                thread.Join();
                semaphore.Release();

                if (err != null)
                    Console.WriteLine($"\n  ゲーム{gLocal} エラー: {err.GetType().Name}: {err.Message}");

                gameLines[gLocal]   = lines;
                gameMoves[gLocal]   = moves;
                gameResults[gLocal] = result;
                int done = Interlocked.Increment(ref completed);
                long totalSamples = Interlocked.Add(ref sampleCount, lines.Count);
                int s, g2, d;
                lock (printLock)
                {
                    if      (result > 0) 先手勝ち++;
                    else if (result < 0) 後手勝ち++;
                    else                 引き分け++;
                    s = 先手勝ち; g2 = 後手勝ち; d = 引き分け;
                }

                double elapsed = sw.Elapsed.TotalSeconds;
                double eta = done > 0 ? elapsed / done * (numGames - done) : 0;
                lock (printLock)
                {
                    Console.Write(
                        $"\r  {Bar(done, numGames)}  {done}/{numGames}  {(double)done/numGames*100:F0}%  " +
                        $"eta {FmtTime(eta)}  {totalSamples:N0} サンプル  経過 {FmtTime(elapsed)}  " +
                        $"先手勝:{s} 後手勝:{g2} 引分:{d}");
                }
            });
        }

        Task.WaitAll(tasks);

        Console.WriteLine();

        // 勝敗サマリー
        double tot = 先手勝ち + 後手勝ち + 引き分け;
        double avgMoves = gameMoves.Average();
        Console.WriteLine($"  先手勝:{先手勝ち}({先手勝ち/tot*100:F1}%)  後手勝:{後手勝ち}({後手勝ち/tot*100:F1}%)  引分:{引き分け}({引き分け/tot*100:F1}%)  平均{avgMoves:F1}手");

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
