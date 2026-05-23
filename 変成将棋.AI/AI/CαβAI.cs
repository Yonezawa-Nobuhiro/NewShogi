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

// ── αβ探索 AI ────────────────────────────────────────────────────────

/// <summary>
/// 反復深化 + αβ枝刈り + 手順並べ替え（駒取り優先）による探索 AI。
/// 評価関数は C評価関数（駒得・王危険度・位置ボーナス）を使用。
/// パラメータは αβパラメータ.json で調整可能。
/// </summary>
public sealed class CαβAI : IプレイヤーAI
{
    private const int 詰点数 = 10_000_000;

    private readonly αβパラメータ _p;
    private readonly int[] _駒価値 = new int[17];
    private readonly C定跡書? _定跡書;
    private readonly CNNUE評価器?     _nnue;   // HalfKP NNUE（nnue_weights_halfkp.bin）
    private readonly CNNUE評価器Int8? _nnue8;  // INT8 版（将来用）

    // アキュムレータスタック（インクリメンタル更新用）
    private readonly float[][]? _加算器_先手;    // [探索深さ+2][L1数]
    private readonly float[][]? _加算器_後手;    // [探索深さ+2][L1数]
    private readonly int[]?     _局面区分_先手;    // バケット番号スタック（先手視点）
    private readonly int[]?     _局面区分_後手;    // バケット番号スタック（後手視点）

    public CαβAI(string? paramsPath = null, string? bookPath = null)
    {
        _p = αβパラメータ.Load(paramsPath);
        _定跡書 = C定跡書.Load(bookPath);

        var dir = paramsPath != null
            ? Path.GetDirectoryName(Path.GetFullPath(paramsPath)) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        _nnue  = CNNUE評価器.Load(Path.Combine(dir, "nnue_weights_halfkp.bin"));
        _nnue8 = CNNUE評価器Int8.Load(Path.Combine(dir, "nnue_weights_int8.bin"));

        if (_nnue != null)
        {
            int stackSize = _p.探索深さ + 2;
            _加算器_先手 = new float[stackSize][];
            _加算器_後手 = new float[stackSize][];
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

    /// <summary>最善手と評価値を1回の探索で返す。</summary>
    public (S手? 最善手, int 点数) Get手とスコア(C盤面 盤面)
    {
        if (_定跡書 != null)
        {
            var 定跡手 = _定跡書.QueryS手(盤面);
            if (定跡手.HasValue) return (定跡手.Value, 0);
        }

        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        if (n == 0) return (null, -詰点数);

        S手 最善手 = buf[0];
        int 点数   = 0;
        for (int 深さ = 1; 深さ <= _p.探索深さ; 深さ++)
            (最善手, 点数) = SearchRoot(盤面, buf[..n], 深さ);

        return (最善手, 点数);
    }

    public void Dispose() { }

    // ── ルートノード探索 ──────────────────────────────────────────────

    private (S手 最善手, int 点数) SearchRoot(C盤面 盤面, Span<S手> 候補手集, int 深度)
    {
        S手 最善手 = 候補手集[0];
        int α  = -詰点数;
        int n  = 候補手集.Length;

        if (_nnue != null)
        {
            var 先手玉種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
            var 後手玉種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;
            _局面区分_先手![0] = CNNUE評価器.局面区分番号取得(先手玉種, 後手玉種);
            _局面区分_後手![0] = CNNUE評価器.局面区分番号取得(後手玉種, 先手玉種);
            _nnue.加算器計算(盤面, E手番.先手, _局面区分_先手[0], _加算器_先手![0]);
            _nnue.加算器計算(盤面, E手番.後手, _局面区分_後手[0], _加算器_後手![0]);
        }

        Span<int> 点数集 = stackalloc int[n];
        for (int i = 0; i < n; i++) 点数集[i] = ScoreMove(盤面, 候補手集[i]);

        for (int i = 0; i < n; i++)
        {
            int best = i;
            for (int j = i + 1; j < n; j++)
                if (点数集[j] > 点数集[best]) best = j;
            if (best != i)
            {
                (候補手集[i],  候補手集[best])  = (候補手集[best],  候補手集[i]);
                (点数集[i], 点数集[best]) = (点数集[best], 点数集[i]);
            }

            var 取消 = 盤面.Apply(候補手集[i]);
            if (_nnue != null)
            {
                Copy加算器(0);
                Update加算器(盤面, 0, 候補手集[i], 取消);
            }
            int 点数 = -Search(盤面, 深度 - 1, -詰点数, -α, 1);
            盤面.Undo(候補手集[i], 取消);

            if (点数 > α)
            {
                α  = 点数;
                最善手 = 候補手集[i];
            }
        }
        return (最善手, α);
    }

    // ── Negamax + αβ枝刈り ───────────────────────────────────────────
    // 内部ノードは Generate擬似合法手 + Apply後インライン王手放置チェックを使う。
    // これにより Get合法手（擬似生成+フィルタで Apply/Undo 2倍）を避け約2倍速になる。

    private int Search(C盤面 盤面, int 残深さ, int α, int β, int 加算器深度)
    {
        // 残深さがもはやない場合
        if (残深さ <= 0)
        {
            return _nnue != null
                ? _nnue.加算器から評価(_加算器_先手![加算器深度], _加算器_後手![加算器深度], 盤面.手番)
                : C評価関数.Evaluate(盤面, _p, _駒価値);
        }

        Span<S手> 候補手集 = stackalloc S手[C合法手生成器.最大手数];
        int 合法手数 = C合法手生成器.Generate擬似合法手(盤面, 候補手集);

        Span<int> scores = stackalloc int[合法手数];
        for (int i = 0; i < 合法手数; i++)
            scores[i] = ScoreMove(盤面, 候補手集[i]);

        bool 合法手あり = false;
        for (int i = 0; i < 合法手数; i++){
            int best = i;
            for (int j = i + 1; j < 合法手数; j++)
                if (scores[j] > scores[best]) best = j;
            if (best != i)
            {
                (候補手集[i],  候補手集[best])  = (候補手集[best],  候補手集[i]);
                (scores[i], scores[best]) = (scores[best], scores[i]);
            }

            var 取消 = 盤面.Apply(候補手集[i]);
            if (!C合法手生成器.Is自玉安全(盤面, 盤面.手番))
            {
                盤面.Undo(候補手集[i], 取消);
                continue;
            }
            合法手あり = true;
            if (_nnue != null)
            {
                Copy加算器(加算器深度);
                Update加算器(盤面, 加算器深度, 候補手集[i], 取消);
            }
            int score = -Search(盤面, 残深さ - 1, -β, -α, 加算器深度 + 1);
            盤面.Undo(候補手集[i], 取消);
            if (score >= β) return β;
            if (score > α)  α = score;
        }

        if (!合法手あり)
            return -詰点数;
        else
            return α;
    }

    // ── アキュムレータ差分更新ヘルパー ──────────────────────────────────

    private void Copy加算器(int 親深さ)
    {
        Array.Copy(_加算器_先手![親深さ], _加算器_先手[親深さ + 1], CNNUE評価器.L1数);
        Array.Copy(_加算器_後手![親深さ], _加算器_後手[親深さ + 1], CNNUE評価器.L1数);
    }

    private void Update加算器(C盤面 盤面, int 親深さ, S手 手, S取消情報 取消)
    {
        int 子深さ = 親深さ + 1;
        var 先手玉種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
        var 後手玉種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;
        int 新先手局面区分 = CNNUE評価器.局面区分番号取得(先手玉種, 後手玉種);
        int 新後手局面区分 = CNNUE評価器.局面区分番号取得(後手玉種, 先手玉種);
        _nnue!.加算器更新(盤面, E手番.先手, _局面区分_先手![親深さ], 新先手局面区分, _加算器_先手![子深さ], 手, 取消);
        _nnue!.加算器更新(盤面, E手番.後手, _局面区分_後手![親深さ], 新後手局面区分, _加算器_後手![子深さ], 手, 取消);
        _局面区分_先手[子深さ] = 新先手局面区分;
        _局面区分_後手[子深さ] = 新後手局面区分;
    }

    // ── 手順スコアリング（遅延選択ソート用）─────────────────────────

    private int ScoreMove(C盤面 盤面, S手 手)
    {
        if (手.Is打ち) return 0;
        var 取り = 盤面.Get駒(手.Get移動先);
        return 取り != null ? _駒価値[(int)取り.種類] : 手.Is成り ? 50 : 0;
    }
}
