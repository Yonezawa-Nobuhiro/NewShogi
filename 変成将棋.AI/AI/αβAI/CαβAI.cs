using System.IO;
using 変成将棋.Models;
using 変成将棋.NNUE;
using static 変成将棋.AI.αβAI.αβパラメータ;

namespace 変成将棋.AI.αβAI;

/// <summary>
/// 反復深化 + αβ枝刈り + 置換表 + 手順並べ替り（TT最善手・駒取り優先）による探索 AI。
///
/// 評価関数の優先順位:
///   1. CNNUE評価器HalfKPInt8 (NHKI形式: nnue_weights_halfkp_i8.bin) — INT16加算器+pmaddubsw
///   2. CNNUE評価器            (NHKP形式: nnue_weights_halfkp.bin)   — float32
///   3. C評価関数              (静的評価: 駒得・王危険度・位置ボーナス)
/// </summary>
public sealed partial class CαβAI : IプレイヤーAI
{
    private const int 詰点数 = 10_000_000;

    private readonly αβパラメータ _p;
    private readonly int[] _駒価値 = new int[17];
    private readonly C定跡書? _定跡書;
    private readonly C置換表 _置換表;

    // デバッグ統計
    internal long _stat_nodes, _stat_ttヒット, _stat_nmp発動, _stat_lmr適用;
    internal long _stat_qnodes, _stat_q_futility;

    public long StatNodes  => _stat_nodes;
    public long StatQNodes => _stat_qnodes;
    internal long _stat_qsee_checked, _stat_qsee_skipped;
    internal long _stat_qskip_α;
    internal int  _stat_qdepth_max;

    // History Heuristic: [移動元byte][移動先byte] のβカット蓄積スコア
    private readonly int[,] _history = new int[256, 256];
    // Killer Move: 深さごとに最大2手保持
    private const int KillerSlots = 2;
    private readonly S手[,] _killer;

    // 千日手検出: 探索中の局面ハッシュ履歴
    private readonly ulong[] _ハッシュ履歴 = new ulong[512];
    private int _履歴数 = 0;

    // Lazy Update: 加算器を Apply 直後ではなく Eval 時に初めて更新する
    // SetDirty → Eval 時に Refresh加算器 → 加算器更新という流れ
    private readonly bool[]? _加算器dirty;
    private readonly S手[]? _lazy手;
    private readonly S取消情報[]? _lazy取消;

    // NNUE 評価器（どちらか片方のみロードされる）
    private readonly CNNUE評価器HalfKPInt8? _nnue_i8;  // INT8 優先
    private readonly CNNUE評価器?           _nnue;     // float フォールバック

    // アキュムレータスタック — INT8用 (short) または float用 で排他利用
    private readonly short[][]? _加算器_先手_i8;
    private readonly short[][]? _加算器_後手_i8;
    private readonly float[][]? _加算器_先手;
    private readonly float[][]? _加算器_後手;

    // バケット番号スタック（INT8/float 共通）
    private readonly int[]? _局面区分_先手;
    private readonly int[]? _局面区分_後手;

    public CαβAI(string? paramsPath = null, string? bookPath = null, bool nnueEnabled = true)
    {
        _置換表 = new();
        _p           = αβパラメータ.Load(paramsPath);
        _定跡書      = C定跡書.Load(bookPath);

        if (nnueEnabled)
        {
            var dir = paramsPath != null
                ? Path.GetDirectoryName(Path.GetFullPath(paramsPath)) ?? AppContext.BaseDirectory
                : AppContext.BaseDirectory;

            // INT8 優先ロード
            _nnue_i8 = CNNUE評価器HalfKPInt8.Load(Path.Combine(dir, "nnue_weights_halfkp_i8.bin"));
            if (_nnue_i8 == null)
                _nnue = CNNUE評価器.Load(Path.Combine(dir, "nnue_weights_halfkp.bin"));
        }

        int 最大探索深さ = _p.思考時間ms > 0 ? 99 : _p.探索深さ;
        int stackSize = 最大探索深さ + 12;  // +10: Quiesce 最大深さ + 余裕2
        _killer = new S手[最大探索深さ + 2, KillerSlots];

        if (_nnue_i8 != null || _nnue != null)
        {
            _加算器dirty = new bool[stackSize];
            _lazy手      = new S手[stackSize];
            _lazy取消    = new S取消情報[stackSize];
        }

        if (_nnue_i8 != null)
        {
            _加算器_先手_i8 = new short[stackSize][];
            _加算器_後手_i8 = new short[stackSize][];
            _局面区分_先手  = new int[stackSize];
            _局面区分_後手  = new int[stackSize];
            for (int d = 0; d < stackSize; d++)
            {
                _加算器_先手_i8[d] = new short[CNNUE評価器HalfKPInt8.L1数];
                _加算器_後手_i8[d] = new short[CNNUE評価器HalfKPInt8.L1数];
            }
        }
        else if (_nnue != null)
        {
            _加算器_先手   = new float[stackSize][];
            _加算器_後手   = new float[stackSize][];
            _局面区分_先手 = new int[stackSize];
            _局面区分_後手 = new int[stackSize];
            for (int d = 0; d < stackSize; d++)
            {
                _加算器_先手[d] = new float[CNNUE評価器.L1数];
                _加算器_後手[d] = new float[CNNUE評価器.L1数];
            }
        }

        var v = _p.駒価値;
        _駒価値[(int)E駒種.歩兵] = v.歩兵;
        _駒価値[(int)E駒種.香車] = v.香車;
        _駒価値[(int)E駒種.桂馬] = v.桂馬;
        _駒価値[(int)E駒種.銀将] = v.銀将;
        _駒価値[(int)E駒種.金将] = v.金将;
        _駒価値[(int)E駒種.角行] = v.角行;
        _駒価値[(int)E駒種.飛車] = v.飛車;
        _駒価値[(int)E駒種.玉将] = 0;
        _駒価値[(int)E駒種.と金] = v.と金;
        _駒価値[(int)E駒種.竪行] = v.竪行;
        _駒価値[(int)E駒種.騎兵] = v.騎兵;
        _駒価値[(int)E駒種.麒麟] = v.麒麟;
        _駒価値[(int)E駒種.鳳凰] = v.鳳凰;
        _駒価値[(int)E駒種.龍馬] = v.龍馬;
        _駒価値[(int)E駒種.龍王] = v.龍王;
        _駒価値[(int)E駒種.獅王] = v.獅王;
    }

    // ── IプレイヤーAI ─────────────────────────────────────────────────

    public S手? Get手(C盤面 盤面) => Get手とスコア(盤面).最善手;

    /// <summary>現局面を αβ 探索して手番側視点の評価値を返す。</summary>
    public int Evaluate(C盤面 盤面) => Get手とスコア(盤面).点数;

    /// <summary>最善手と評価値を返す。</summary>
    public (S手? 最善手, int 点数) Get手とスコア(C盤面 盤面)
    {
        if (_定跡書 != null)
        {
            var 定跡手 = _定跡書.QueryS手(盤面);
            if (定跡手.HasValue) return (定跡手.Value, 0);
        }

        // 思考時間制限あり: タイマーで CancellationToken を発火
        if (_p.思考時間ms > 0)
        {
            using var cts = new System.Threading.CancellationTokenSource(_p.思考時間ms);
            return Get手とスコアSingle(盤面, cts.Token);
        }

        return Get手とスコアSingle(盤面);
    }

    private (S手? 最善手, int 点数) Get手とスコアSingle(C盤面 盤面, CancellationToken ct = default)
    {
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        if (n == 0) return (null, -詰点数);

        // 思考時間ms > 0 のとき探索深さの上限を緩め、時間切れで止まるまで深く探索する
        int 最大深さ = _p.思考時間ms > 0 ? 99 : _p.探索深さ;

        _stat_nodes = _stat_ttヒット = _stat_nmp発動 = _stat_lmr適用 = 0;
        _stat_qnodes = _stat_q_futility = _stat_qsee_checked = _stat_qsee_skipped = _stat_qskip_α = 0;
        _stat_qdepth_max = 0;

        // ルート局面をハッシュ履歴に登録（千日手検出の起点）
        _履歴数 = 0;
        _ハッシュ履歴[_履歴数++] = 盤面.αβハッシュ;

        // ルートで詰み探索
        {
            var 詰み = C詰将棋探索.詰み探索(盤面, _p.詰み探索手数, ct);
            if (詰み.HasValue) return (詰み.Value.初手, 詰点数 - 詰み.Value.手数);
        }

        S手 最善手 = buf[0];
        int 点数   = 0;
        const int AspirationDelta = 50;
        for (int 深さ = 1; 深さ <= 最大深さ && !ct.IsCancellationRequested; 深さ++)
        {
            if (深さ <= 2)
            {
                (最善手, 点数) = SearchRoot(盤面, buf[..n], 深さ, -詰点数, 詰点数, ct);
            }
            else
            {
                int α = 点数 - AspirationDelta;
                int β = 点数 + AspirationDelta;
                int delta = AspirationDelta;
                while (!ct.IsCancellationRequested)
                {
                    (S手 候補手, int s) = SearchRoot(盤面, buf[..n], 深さ, α, β, ct);
                    if (ct.IsCancellationRequested) break;
                    if (s > α && s < β) { 最善手 = 候補手; 点数 = s; break; }
                    // α/β を広げる。境界に張り付いて変わらなくなったら採用して抜ける
                    int newα = s <= α ? Math.Max(-詰点数, α - delta) : α;
                    int newβ = s >= β ? Math.Min(詰点数,  β + delta) : β;
                    if (newα == α && newβ == β) { 最善手 = 候補手; 点数 = s; break; }
                    α = newα; β = newβ; delta *= 2;
                }
            }
        }
        _履歴数 = 0;

        if (System.Diagnostics.Debugger.IsAttached ||
            System.Environment.GetEnvironmentVariable("STAT") == "1")
        {
            long n2 = _stat_nodes;
            Console.Error.WriteLine(
                $"  nodes={n2:N0}  TT={_stat_ttヒット:N0}({(n2>0?_stat_ttヒット*100.0/n2:0):F1}%)  NMP={_stat_nmp発動:N0}  LMR={_stat_lmr適用:N0}");
            long qn = _stat_qnodes;
            Console.Error.WriteLine(
                $"  qnodes={qn:N0}  q_futility={_stat_q_futility:N0}({(qn>0?_stat_q_futility*100.0/qn:0):F1}%)  qdepth_max={_stat_qdepth_max}");
            long sc = _stat_qsee_checked;
            Console.Error.WriteLine(
                $"  qsee_checked={sc:N0}  qsee_skipped={_stat_qsee_skipped:N0}({(sc>0?_stat_qsee_skipped*100.0/sc:0):F1}%)");
            long mn = _stat_nodes;
            Console.Error.WriteLine(
                $"  qskip_α={_stat_qskip_α:N0}({(mn>0?_stat_qskip_α*100.0/mn:0):F1}% of nodes)");
        }

        return (最善手, 点数);
    }

    public void PrintNNUEStat()
    {
        if (_nnue_i8 == null) return;
        long s = _nnue_i8._stat_scratch, d = _nnue_i8._stat_diff;
        long total = s + d;
        Console.Error.WriteLine($"  NNUE: scratch={s:N0} ({(total>0?s*100.0/total:0):F1}%)  diff={d:N0} ({(total>0?d*100.0/total:0):F1}%)");
    }

    public void 対局開始() => _置換表.世代を進める();

    public void Dispose()
    {
        _nnue_i8?.Dispose();
    }
}
