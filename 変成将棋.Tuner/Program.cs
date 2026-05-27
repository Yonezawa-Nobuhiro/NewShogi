using System.Text.Json;
using 変成将棋.AI;
using 変成将棋.AI.αβAI;
using 変成将棋.Models;

// モード 1 (比較):  <paramsA.json> <paramsB.json> <numGames> [seed]
//   → {"wins":X,"draws":Y,"losses":Z}  (A視点)
//
// モード 2 (生成):  generate <params.json> <numGames> <output_dir> [seed]
//   → <output_dir>/<timestamp>_<n>.kf を numGames 局分出力

const int MaxMoves = 250;
const int RandomOpeningMoves = 8;   // 最初の N 手はランダム（多様性確保）

if (args.Length >= 1 && args[0] == "think")
{
    // think <sfen> [params.json]
    string sfen = args[1];
    string tParams = args.Length > 2 && !args[2].StartsWith("--") ? args[2] : "変成将棋.AI/αβパラメータ.json";
    bool tNnue = !args.Contains("--no-nnue");
    var board = new C盤面(sfen);
    using var ai = new CαβAI(tParams, nnueEnabled: tNnue);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var 手 = ai.Get手(board);
    sw.Stop();
    ai.PrintNNUEStat();
    if (手 == null)
        Console.WriteLine($"  最善手: (なし)  ({sw.Elapsed.TotalMilliseconds:F0} ms)");
    else if (手.Value.Is打ち)
        Console.WriteLine($"  最善手: {手.Value.Get打ち駒}打ち→{手.Value.Get移動先.列}{手.Value.Get移動先.段}  ({sw.Elapsed.TotalMilliseconds:F0} ms)");
    else
        Console.WriteLine($"  最善手: {手.Value.Get移動元.列}{手.Value.Get移動元.段}→{手.Value.Get移動先.列}{手.Value.Get移動先.段}{(手.Value.Is成り ? "成" : "")}  ({sw.Elapsed.TotalMilliseconds:F0} ms)");
    return 0;
}

if (args.Length >= 1 && args[0] == "benchmark")
{
    // benchmark [params.json] [--no-nnue] [--save <path.kf>] [--max <moves>]
    bool nnue = !args.Contains("--no-nnue");
    string bParams = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "変成将棋.AI/αβパラメータ.json";
    int saveIdx = Array.IndexOf(args, "--save");
    string? kifPath = saveIdx >= 0 && saveIdx + 1 < args.Length ? args[saveIdx + 1] : null;
    int maxIdx = Array.IndexOf(args, "--max");
    int maxMoves = maxIdx >= 0 && maxIdx + 1 < args.Length ? int.Parse(args[maxIdx + 1]) : 250;
    Benchmark.Run(bParams, nnue, kifPath, maxMoves);
    return 0;
}

if (args.Length >= 1 && args[0] == "eval_data")
{
    // eval_data <params.json> <numGames> <output.tsv> [seed]
    if (args.Length < 4) { Console.Error.WriteLine("Usage: eval_data <params.json> <numGames> <output.tsv> [seed]"); return 1; }
    EvalDataGen.Run(args[1], int.Parse(args[2]), args[3], args.Length > 4 ? int.Parse(args[4]) : 0);
    return 0;
}

if (args.Length >= 1 && args[0] == "win_rate")
{
    // win_rate <params.json> <numGames> [seed] [--nnue] — 自己対局で先手/後手勝率を集計
    if (args.Length < 3) { Console.Error.WriteLine("Usage: win_rate <params.json> <numGames> [seed] [--nnue]"); return 1; }
    string wrParams  = args[1];
    int    wrGames   = int.Parse(args[2]);
    int    wrSeed    = args.Length > 3 && args[3] != "--nnue" ? int.Parse(args[3]) : 0;
    bool   wrNnue    = args.Contains("--nnue");
    int    sente勝ち = 0, gote勝ち = 0, 引き分け = 0;
    int    completed = 0;
    var    gameLock  = new object();
    Console.Error.WriteLine($"  NNUE: {wrNnue}");

    Parallel.For(0, wrGames, g =>
    {
        using var ai先手 = new CαβAI(wrParams, "NOBOOK", nnueEnabled: wrNnue);
        using var ai後手 = new CαβAI(wrParams, "NOBOOK", nnueEnabled: wrNnue);
        var board = new C盤面();
        board.Reset();
        var rng = new Random(wrSeed + g);

        int result = 0;
        for (int m = 0; m < 400; m++)
        {
            S手? 手;
            if (m < 8)
            {
                S手[] buf = new S手[600];
                int cnt = C合法手生成器.Get合法手(board, buf);
                if (cnt == 0) { result = board.手番 == E手番.先手 ? -1 : 1; break; }
                手 = buf[rng.Next(cnt)];
            }
            else
            {
                var current = board.手番 == E手番.先手 ? ai先手 : ai後手;
                手 = current.Get手(board);
                if (手 == null) { result = board.手番 == E手番.先手 ? -1 : 1; break; }
            }
            board.Apply(手.Value);
        }

        lock (gameLock)
        {
            if      (result > 0) sente勝ち++;
            else if (result < 0) gote勝ち++;
            else                 引き分け++;
            completed++;
            if (completed % Math.Max(1, wrGames / 10) == 0 || completed == wrGames)
                Console.Error.Write($"\r  [{completed}/{wrGames}] 先手:{sente勝ち} 後手:{gote勝ち} 引分:{引き分け}  ");
        }
    });

    Console.Error.WriteLine();
    double total = sente勝ち + gote勝ち + 引き分け;
    Console.WriteLine($"先手勝率: {sente勝ち/total*100:F1}%  後手勝率: {gote勝ち/total*100:F1}%  引分: {引き分け/total*100:F1}%");
    Console.WriteLine(JsonSerializer.Serialize(new { sente勝ち, gote勝ち, 引き分け }));
    return 0;
}

if (args.Length >= 1 && args[0] == "generate")
{
    // ── 生成モード ─────────────────────────────────────────────────────
    if (args.Length < 4)
    {
        Console.Error.WriteLine("Usage: generate <params.json> <numGames> <output_dir> [seed=0]");
        return 1;
    }
    string paramsPath = args[1];
    int numGames      = int.Parse(args[2]);
    string outDir     = args[3];
    int seed          = args.Length > 4 ? int.Parse(args[4]) : 0;

    Directory.CreateDirectory(outDir);

    int genCompleted = 0;
    var genLock = new object();

    Parallel.For(0, numGames, new ParallelOptions { MaxDegreeOfParallelism = 6 }, g =>
    {
        using var ai先手 = new CαβAI(paramsPath, "NOBOOK");
        using var ai後手 = new CαβAI(paramsPath, "NOBOOK");

        var board = new C盤面();
        board.Reset();
        var rng = new Random(seed + g);

        var sfens = new List<string> { board.ToSFEN() };

        for (int m = 0; m < MaxMoves; m++)
        {
            S手? 手;
            if (m < RandomOpeningMoves)
            {
                S手[] legalBuf = new S手[600];
                int legalCount = C合法手生成器.Get合法手(board, legalBuf);
                if (legalCount == 0) break;
                手 = legalBuf[rng.Next(legalCount)];
            }
            else
            {
                var current = board.手番 == E手番.先手 ? ai先手 : ai後手;
                手 = current.Get手(board);
                if (!手.HasValue) break;
            }
            board.Apply(手.Value);
            sfens.Add(board.ToSFEN());
        }

        // .kf 形式で保存（ファイル名は g で一意化）
        string path = Path.Combine(outDir, $"αβ_{g:D4}.kf");
        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine("# 変成将棋棋譜");
        w.WriteLine($"# 日時: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"# 手数: {sfens.Count - 1}");
        w.WriteLine();
        foreach (var s in sfens) w.WriteLine(s);

        lock (genLock)
        {
            genCompleted++;
            Console.Error.WriteLine($"  [{genCompleted}/{numGames}] {path}");
        }
    });

    Console.Error.WriteLine("\n完了");
    return 0;
}

// ── 比較モード ──────────────────────────────────────────────────────────
if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: <paramsA.json> <paramsB.json> <numGames> [seed=0]");
    Console.Error.WriteLine("       generate <params.json> <numGames> <output_dir> [seed=0]");
    return 1;
}

string paramsA  = args[0];
string paramsB  = args[1];
int    games    = int.Parse(args[2]);
int    gameSeed = args.Length > 3 ? int.Parse(args[3]) : 0;
int    wins = 0, draws = 0, losses = 0;

for (int g = 0; g < games; g++)
{
    bool aIs先手   = (g % 2 == 0);
    string 先手P   = aIs先手 ? paramsA : paramsB;
    string 後手P   = aIs先手 ? paramsB : paramsA;

    using var ai先手 = new CαβAI(先手P, "NOBOOK");
    using var ai後手 = new CαβAI(後手P, "NOBOOK");

    var board = new C盤面();
    board.Reset();

    int result = 0;
    for (int m = 0; m < MaxMoves; m++)
    {
        var current = board.手番 == E手番.先手 ? ai先手 : ai後手;
        var 手 = current.Get手(board);
        if (手 == null) { result = board.手番 == E手番.先手 ? -1 : 1; break; }
        board.Apply(手.Value);
    }

    int aResult = aIs先手 ? result : -result;
    if      (aResult >  0) wins++;
    else if (aResult <  0) losses++;
    else                   draws++;

    Console.Error.Write($"\r  [{g + 1}/{games}] {wins}W {draws}D {losses}L   ");
}

Console.Error.WriteLine();
Console.WriteLine(JsonSerializer.Serialize(new { wins, draws, losses }));
return 0;
