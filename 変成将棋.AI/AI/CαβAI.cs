using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using 変成将棋.Models;
using 変成将棋.NNUE;

namespace 変成将棋.AI;

// ── パラメータ ────────────────────────────────────────────────────────

public record 駒価値設定
{
    [JsonPropertyName("歩兵")]  public int 歩兵  { get; init; } = 100;
    [JsonPropertyName("香車")]  public int 香車  { get; init; } = 400;
    [JsonPropertyName("桂馬")]  public int 桂馬  { get; init; } = 450;
    [JsonPropertyName("銀将")]  public int 銀将  { get; init; } = 600;
    [JsonPropertyName("金将")]  public int 金将  { get; init; } = 700;
    [JsonPropertyName("角行")]  public int 角行  { get; init; } = 800;
    [JsonPropertyName("飛車")]  public int 飛車  { get; init; } = 1000;
    [JsonPropertyName("と金")]  public int と金  { get; init; } = 600;
    [JsonPropertyName("竪行")]  public int 竪行  { get; init; } = 700;
    [JsonPropertyName("騎兵")]  public int 騎兵  { get; init; } = 650;
    [JsonPropertyName("麒麟")]  public int 麒麟  { get; init; } = 800;
    [JsonPropertyName("鳳凰")]  public int 鳳凰  { get; init; } = 850;
    [JsonPropertyName("龍馬")]  public int 龍馬  { get; init; } = 1050;
    [JsonPropertyName("龍王")]  public int 龍王  { get; init; } = 1200;
    [JsonPropertyName("獅王")]  public int 獅王  { get; init; } = 0;    // 王扱い
}

public record αβパラメータ
{
    [JsonPropertyName("探索深さ")]         public int 探索深さ         { get; init; } = 6;
    [JsonPropertyName("王危険度重み")]     public int 王危険度重み     { get; init; } = 80;
    [JsonPropertyName("位置ボーナス重み")] public int 位置ボーナス重み { get; init; } = 30;
    [JsonPropertyName("持ち駒ボーナス重み")] public int 持ち駒ボーナス重み { get; init; } = 20;
    [JsonPropertyName("攻め込み重み")]     public int 攻め込み重み     { get; init; } = 15;
    [JsonPropertyName("打ち込みポテンシャル重み")] public int 打ち込みポテンシャル重み { get; init; } = 8;
    [JsonPropertyName("駒価値")]           public 駒価値設定 駒価値     { get; init; } = new();
    [JsonPropertyName("思考時間ms")]       public int 思考時間ms       { get; init; } = 0;  // 0=深さ固定

    private static readonly JsonSerializerOptions _jsonOpt =
        new() { ReadCommentHandling = JsonCommentHandling.Skip };

    public static αβパラメータ Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "αβパラメータ.json");
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
        return JsonSerializer.Deserialize<αβパラメータ>(json, _jsonOpt) ?? new();
    }
}

// ── 置換表 ───────────────────────────────────────────────────────────

public struct C置換表エントリ
{
    public uint ハッシュ上位;  // ulong ハッシュの上位 32bit（衝突検出）
    public int  スコア;
    public byte 深さ;
    public byte フラグ;        // 0=完全, 1=下限(βカット), 2=上限(αに届かず)
    public S手  最善手;
}

public sealed class C置換表
{
    internal const byte 完全 = 0, 下限 = 1, 上限 = 2;

    private readonly C置換表エントリ[] _表;
    private readonly ulong _マスク;

    internal C置換表(int 対数 = 20)   // 2^20 ≈ 1Mエントリ ≒ 14MB
    {
        _表   = new C置換表エントリ[1 << 対数];
        _マスク = (ulong)(_表.Length - 1);
    }

    // ヒットかつ十分な深さなら true とスコアを返す。最善手は常に返す（手順並べ替え用）。
    internal bool 検索(ulong h, int 深さ, int α, int β, out int スコア, out S手 最善手)
    {
        ref var e = ref _表[h & _マスク];
        最善手 = e.最善手;
        スコア  = 0;
        if (e.ハッシュ上位 != (uint)(h >> 32)) return false;
        if (e.深さ < 深さ)                     return false;
        スコア = e.スコア;
        if (e.フラグ == 完全)              return true;
        if (e.フラグ == 下限 && スコア >= β) return true;
        if (e.フラグ == 上限 && スコア <= α) return true;
        return false;
    }

    internal void 保存(ulong h, int 深さ, int スコア, byte フラグ, S手 最善手)
    {
        ref var e = ref _表[h & _マスク];
        // 同じ局面でより深い完全スコアがあれば上書きしない
        if (e.ハッシュ上位 == (uint)(h >> 32) && e.深さ > 深さ && e.フラグ == 完全) return;
        e.ハッシュ上位 = (uint)(h >> 32);
        e.スコア       = スコア;
        e.深さ         = (byte)Math.Min(深さ, 255);
        e.フラグ        = フラグ;
        e.最善手        = 最善手;
    }
}

// ── αβ探索 AI ────────────────────────────────────────────────────────

/// <summary>
/// 反復深化 + αβ枝刈り + 置換表 + 手順並べ替り（TT最善手・駒取り優先）による探索 AI。
///
/// 評価関数の優先順位:
///   1. CNNUE評価器HalfKPInt8 (NHKI形式: nnue_weights_halfkp_i8.bin) — INT16加算器+pmaddubsw
///   2. CNNUE評価器            (NHKP形式: nnue_weights_halfkp.bin)   — float32
///   3. C評価関数              (静的評価: 駒得・王危険度・位置ボーナス)
/// </summary>
public sealed class CαβAI : IプレイヤーAI
{
    private const int 詰点数 = 10_000_000;

    private readonly αβパラメータ _p;
    private readonly int[] _駒価値 = new int[17];
    private readonly C定跡書? _定跡書;
    private readonly C置換表 _置換表;

    // デバッグ統計
    internal long _stat_nodes, _stat_ttヒット, _stat_nmp発動, _stat_lmr適用;

    // History Heuristic: [移動元byte][移動先byte] のβカット蓄積スコア
    private readonly int[,] _history = new int[256, 256];
    // Killer Move: 深さごとに最大2手保持
    private const int KillerSlots = 2;
    private readonly S手[,] _killer;

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
        int stackSize = 最大探索深さ + 22;  // +20: Quiescence Search の取り合い分
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

        S手 最善手 = buf[0];
        int 点数   = 0;
        for (int 深さ = 1; 深さ <= 最大深さ && !ct.IsCancellationRequested; 深さ++)
            (最善手, 点数) = SearchRoot(盤面, buf[..n], 深さ, ct);

        if (System.Diagnostics.Debugger.IsAttached ||
            System.Environment.GetEnvironmentVariable("STAT") == "1")
        {
            long n2 = _stat_nodes;
            Console.Error.WriteLine(
                $"  nodes={n2:N0}  TT={_stat_ttヒット:N0}({(n2>0?_stat_ttヒット*100.0/n2:0):F1}%)  NMP={_stat_nmp発動:N0}  LMR={_stat_lmr適用:N0}");
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

    public void Dispose()
    {
        _nnue_i8?.Dispose();
    }

    // ── ルートノード探索 ──────────────────────────────────────────────

    private (S手 最善手, int 点数) SearchRoot(C盤面 盤面, Span<S手> 候補手集, int 深度,
                                              CancellationToken ct)
    {
        S手 最善手 = 候補手集[0];
        int α  = -詰点数;
        int n  = 候補手集.Length;

        // ルートの加算器を初期化
        Init加算器Root(盤面);

        Span<int> 点数集 = stackalloc int[n];
        for (int i = 0; i < n; i++) 点数集[i] = ScoreMove(盤面, 候補手集[i], 0);

        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested) break;

            // 遅延選択ソート
            int best = i;
            for (int j = i + 1; j < n; j++)
                if (点数集[j] > 点数集[best]) best = j;
            if (best != i)
            {
                (候補手集[i], 候補手集[best]) = (候補手集[best], 候補手集[i]);
                (点数集[i],   点数集[best])   = (点数集[best],   点数集[i]);
            }

            var 取消 = 盤面.Apply(候補手集[i]);
            SetDirty(1, 候補手集[i], 取消);
            int 点数 = -Search(盤面, 深度 - 1, -詰点数, -α, 1, ct);
            盤面.Undo(候補手集[i], 取消);

            if (点数 > α)
            {
                α     = 点数;
                最善手 = 候補手集[i];
            }
        }
        return (最善手, α);
    }

    // ── 静止探索（駒取りのみ継続して局面を安定させる） ────────────────────

    private int Quiesce(C盤面 盤面, int α, int β, int 加算器深度, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return 0;

        // 加算器を子ノードのために更新しておく（評価値は駒価値ベースで代替）
        if (_加算器dirty != null && _加算器dirty[加算器深度])
            Refresh加算器(加算器深度, 盤面);

        // stand-pat: 駒価値ベースの簡易評価（駒取り連鎖局面での NNUE 多重呼び出しを回避）
        int stand_pat = C評価関数.Evaluate(盤面, _p, _駒価値);
        if (stand_pat >= β) return β;
        if (stand_pat > α) α = stand_pat;

        // 駒取り手専用生成器を使用（全手生成より高速）
        Span<S手> 候補手集 = stackalloc S手[C合法手生成器.最大手数];
        Span<int> capScores = stackalloc int[C合法手生成器.最大手数];
        int 合法手数 = C合法手生成器.Generate駒取り手(盤面, 候補手集);

        // 各駒取り手のスコア（取る駒の価値）を計算
        for (int i = 0; i < 合法手数; i++)
        {
            // 獅王2回移動は中間取りの場合があるので取消情報から取得できないが、
            // 移動先に駒があればそれ、なければ中間駒（獅王中取り）を使う
            var 移動先駒 = 盤面.Get駒(候補手集[i].Get移動先);
            if (移動先駒.Is有効)
                capScores[i] = _駒価値[(int)移動先駒.種類];
            else if (!候補手集[i].Is打ち)
            {
                var 中間駒 = 盤面.Get駒(候補手集[i].Get中間);
                capScores[i] = 中間駒.Is有効 ? _駒価値[(int)中間駒.種類] : 0;
            }
            else
                capScores[i] = 0;
        }

        for (int i = 0; i < 合法手数; i++)
        {
            // 遅延選択ソート（大きい駒取りを優先）
            int best = i;
            for (int j = i + 1; j < 合法手数; j++)
                if (capScores[j] > capScores[best]) best = j;
            if (best != i)
            {
                (候補手集[i], 候補手集[best]) = (候補手集[best], 候補手集[i]);
                (capScores[i], capScores[best]) = (capScores[best], capScores[i]);
            }

            // Delta Pruning: ソート済みなのでこの手以降全部 α に届かないなら打ち切り
            if (stand_pat + capScores[i] < α) break;

            var 取消 = 盤面.Apply(候補手集[i]);
            if (!C合法手生成器.Is自玉安全(盤面, 盤面.手番))
            {
                盤面.Undo(候補手集[i], 取消);
                continue;
            }
            SetDirty(加算器深度 + 1, 候補手集[i], 取消);
            int score = -Quiesce(盤面, -β, -α, 加算器深度 + 1, ct);
            盤面.Undo(候補手集[i], 取消);

            if (score >= β) return β;
            if (score > α) α = score;
        }

        return α;
    }

    // ── Negamax + αβ枝刈り + 置換表 ─────────────────────────────────

    private int Search(C盤面 盤面, int 残深さ, int α, int β, int 加算器深度,
                       CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return 0;

        if (残深さ <= 0)
            return Quiesce(盤面, α, β, 加算器深度, ct);

        _stat_nodes++;

        // 置換表ルックアップ
        ulong hash = 盤面.αβハッシュ;
        bool ttヒット = _置換表.検索(hash, 残深さ, α, β, out int ttスコア, out S手 tt最善手);
        if (ttヒット) { _stat_ttヒット++; return ttスコア; }

        // Is自玉安全・staticEvalを1回だけ計算してNMP/Futility両方で共用
        // 自玉不安全（王手）ノードは NMP も Futility も不要なので評価計算をスキップ
        // ただし加算器は子ノードのために更新しておく必要がある
        bool 自玉安全 = C合法手生成器.Is自玉安全(盤面, 盤面.手番);
        int staticEval;
        if (自玉安全)
        {
            staticEval = Eval(加算器深度, 盤面);
        }
        else
        {
            if (_加算器dirty != null && _加算器dirty[加算器深度])
                Refresh加算器(加算器深度, 盤面);
            staticEval = 0;
        }

        // Null Move Pruning: 王手されていない かつ staticEval >= β のとき手番をパスして浅く探索
        if (残深さ >= 3 && 自玉安全 && staticEval >= β)
        {
            _stat_nmp発動++;
            盤面.ApplyNullMove();
            int nullScore = -Search(盤面, 残深さ - 3, -β, -β + 1, 加算器深度, ct);
            盤面.UndoNullMove();
            if (nullScore >= β) return β;
        }

        // Futility Pruning: 浅いノードで staticEval + マージン < α なら静かな手をスキップ
        // 残深さ1=飛車1枚分、残深さ2=飛車2枚分 をマージンとして使用
        int futilityMargin = 残深さ <= 1 ? _駒価値[(int)E駒種.飛車]
                           : 残深さ == 2 ? _駒価値[(int)E駒種.飛車] * 2 : int.MaxValue;
        bool futilityActive = 自玉安全 && futilityMargin < int.MaxValue
                           && staticEval + futilityMargin < α;

        Span<S手> 候補手集 = stackalloc S手[C合法手生成器.最大手数];
        int 合法手数 = C合法手生成器.Generate擬似合法手(盤面, 候補手集);

        Span<int> scores = stackalloc int[合法手数];
        for (int i = 0; i < 合法手数; i++)
            scores[i] = ScoreMove(盤面, 候補手集[i], 加算器深度, tt最善手);

        int 元α = α;
        S手 最善手 = default;
        bool 合法手あり = false;
        for (int i = 0; i < 合法手数; i++)
        {
            int best = i;
            for (int j = i + 1; j < 合法手数; j++)
                if (scores[j] > scores[best]) best = j;
            if (best != i)
            {
                (候補手集[i], 候補手集[best]) = (候補手集[best], 候補手集[i]);
                (scores[i],   scores[best])   = (scores[best],   scores[i]);
            }

            var 取消 = 盤面.Apply(候補手集[i]);
            if (!C合法手生成器.Is自玉安全(盤面, 盤面.手番))
            {
                盤面.Undo(候補手集[i], 取消);
                continue;
            }
            合法手あり = true;

            // Futility Pruning: 静かな手（駒取り・成りでない）をスキップ
            if (futilityActive && scores[i] < 500_000 && !候補手集[i].Is成り)
            {
                盤面.Undo(候補手集[i], 取消);
                continue;
            }

            SetDirty(加算器深度 + 1, 候補手集[i], 取消);

            // LMR: 3手目以降の静かな手（駒取り・TT最善手でない）は depth-2 で探索（深さ5以上で効果的）
            int reduction = (i >= 2 && 残深さ >= 4 && scores[i] < 95) ? 2 : 0;
            if (reduction > 0) _stat_lmr適用++;
            int score = -Search(盤面, 残深さ - 1 - reduction, -β, -α, 加算器深度 + 1, ct);
            // αを超えたらフル深さで再探索
            if (reduction > 0 && score > α)
                score = -Search(盤面, 残深さ - 1, -β, -α, 加算器深度 + 1, ct);

            盤面.Undo(候補手集[i], 取消);
            if (score >= β)
            {
                // 駒取りでない手のβカット → Killer & History 更新
                if (!候補手集[i].Is打ち && !盤面.Get駒(候補手集[i].Get移動先).Is有効)
                    静かな手更新(候補手集[i], 加算器深度, 残深さ);
                _置換表.保存(hash, 残深さ, β, C置換表.下限, 候補手集[i]);
                return β;
            }
            if (score > α)
            {
                α     = score;
                最善手 = 候補手集[i];
            }
        }

        if (!合法手あり) return -詰点数;

        byte フラグ = α > 元α ? C置換表.完全 : C置換表.上限;
        _置換表.保存(hash, 残深さ, α, フラグ, 最善手);
        return α;
    }

    // ── 評価・加算器ヘルパー ──────────────────────────────────────────

    private int Eval(int 加算器深度, C盤面 盤面)
    {
        if (_加算器dirty != null && _加算器dirty[加算器深度])
            Refresh加算器(加算器深度, 盤面);

        if (_nnue_i8 != null)
        {
            return _nnue_i8.加算器から評価(
                _加算器_先手_i8![加算器深度],
                _加算器_後手_i8![加算器深度],
                盤面.手番);
        }
        if (_nnue != null)
        {
            return _nnue.加算器から評価(
                _加算器_先手![加算器深度],
                _加算器_後手![加算器深度],
                盤面.手番);
        }
        return C評価関数.Evaluate(盤面, _p, _駒価値);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void SetDirty(int 子深さ, S手 手, S取消情報 取消)
    {
        if (_加算器dirty == null) return;
        _加算器dirty[子深さ] = true;
        _lazy手![子深さ]   = 手;
        _lazy取消![子深さ] = 取消;
    }

    private void Refresh加算器(int 子深さ, C盤面 盤面)
    {
        int 親深さ = 子深さ - 1;
        Copy加算器(親深さ);
        Update加算器(盤面, 親深さ, _lazy手![子深さ], _lazy取消![子深さ]);
        _加算器dirty![子深さ] = false;
    }

    private void Init加算器Root(C盤面 盤面)
    {
        if (_局面区分_先手 == null) return;
        var 先手玉種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
        var 後手玉種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;
        _局面区分_先手[0] = CNNUE評価器.局面区分番号取得(先手玉種, 後手玉種);
        _局面区分_後手![0] = CNNUE評価器.局面区分番号取得(後手玉種, 先手玉種);

        if (_nnue_i8 != null)
        {
            _nnue_i8.加算器計算(盤面, E手番.先手, _局面区分_先手[0], _加算器_先手_i8![0]);
            _nnue_i8.加算器計算(盤面, E手番.後手, _局面区分_後手[0], _加算器_後手_i8![0]);
        }
        else if (_nnue != null)
        {
            _nnue.加算器計算(盤面, E手番.先手, _局面区分_先手[0], _加算器_先手![0]);
            _nnue.加算器計算(盤面, E手番.後手, _局面区分_後手[0], _加算器_後手![0]);
        }
        if (_加算器dirty != null) _加算器dirty[0] = false;
    }

    private void Copy加算器(int 親深さ)
    {
        if (_nnue_i8 != null)
        {
            Array.Copy(_加算器_先手_i8![親深さ], _加算器_先手_i8[親深さ + 1], CNNUE評価器HalfKPInt8.L1数);
            Array.Copy(_加算器_後手_i8![親深さ], _加算器_後手_i8[親深さ + 1], CNNUE評価器HalfKPInt8.L1数);
        }
        else if (_nnue != null)
        {
            Array.Copy(_加算器_先手![親深さ], _加算器_先手[親深さ + 1], CNNUE評価器.L1数);
            Array.Copy(_加算器_後手![親深さ], _加算器_後手[親深さ + 1], CNNUE評価器.L1数);
        }
    }

    private void Update加算器(C盤面 盤面, int 親深さ, S手 手, S取消情報 取消)
    {
        if (_局面区分_先手 == null) return;
        int 子深さ = 親深さ + 1;
        var 先手玉種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
        var 後手玉種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;
        int 新先手区分 = CNNUE評価器.局面区分番号取得(先手玉種, 後手玉種);
        int 新後手区分 = CNNUE評価器.局面区分番号取得(後手玉種, 先手玉種);

        if (_nnue_i8 != null)
        {
            _nnue_i8.加算器更新(盤面, E手番.先手, _局面区分_先手[親深さ], 新先手区分, _加算器_先手_i8![子深さ], 手, 取消);
            _nnue_i8.加算器更新(盤面, E手番.後手, _局面区分_後手![親深さ], 新後手区分, _加算器_後手_i8![子深さ], 手, 取消);
        }
        else if (_nnue != null)
        {
            _nnue.加算器更新(盤面, E手番.先手, _局面区分_先手[親深さ], 新先手区分, _加算器_先手![子深さ], 手, 取消);
            _nnue.加算器更新(盤面, E手番.後手, _局面区分_後手![親深さ], 新後手区分, _加算器_後手![子深さ], 手, 取消);
        }
        _局面区分_先手[子深さ] = 新先手区分;
        _局面区分_後手![子深さ] = 新後手区分;
    }

    // ── 手順スコアリング ─────────────────────────────────────────────

    private int ScoreMove(C盤面 盤面, S手 手, int 深さIdx, S手 tt最善手 = default)
    {
        // 1. TT最善手
        if (手.移動元 == tt最善手.移動元 && 手.移動先 == tt最善手.移動先 &&
            手.中間   == tt最善手.中間   && 手.手フラグ == tt最善手.手フラグ)
            return 1_000_000;

        // 2. 駒取り（打ち駒は取りなし）
        if (!手.Is打ち)
        {
            var 取り = 盤面.Get駒(手.Get移動先);
            if (取り.Is有効) return 500_000 + _駒価値[(int)取り.種類];
        }

        // 3. Killer Move（静かな手のみ）
        int ki = Math.Min(深さIdx, _killer.GetLength(0) - 1);
        if (手.移動元 == _killer[ki, 0].移動元 && 手.移動先 == _killer[ki, 0].移動先) return 400_000;
        if (手.移動元 == _killer[ki, 1].移動元 && 手.移動先 == _killer[ki, 1].移動先) return 300_000;

        // 4. History + 成りボーナス
        int h = 手.Is打ち ? 0 : _history[手.移動元, 手.移動先];
        return (手.Is成り ? 50 : 0) + h;
    }

    private void 静かな手更新(S手 手, int 深さIdx, int 残深さ)
    {
        if (手.Is打ち) return;
        // Killer: 同じ手でなければシフトして先頭に挿入
        int ki = Math.Min(深さIdx, _killer.GetLength(0) - 1);
        if (手.移動元 != _killer[ki, 0].移動元 || 手.移動先 != _killer[ki, 0].移動先)
        {
            _killer[ki, 1] = _killer[ki, 0];
            _killer[ki, 0] = 手;
        }
        // History: 深さの二乗を加算し、オーバーフロー時に全体を半減
        _history[手.移動元, 手.移動先] += 残深さ * 残深さ;
        if (_history[手.移動元, 手.移動先] > 10_000_000)
            for (int a = 0; a < 256; a++)
                for (int b = 0; b < 256; b++)
                    _history[a, b] >>= 1;
    }
}
