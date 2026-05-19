using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using 変成将棋.Models;

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
    private const int 詰みスコア = 10_000_000;

    private readonly αβパラメータ _p;
    private readonly int[] _駒価値 = new int[17];
    private readonly C定跡書? _定跡書;
    private readonly CNNUE評価器? _nnue;

    // インクリメンタル NNUE アキュムレータスタック（探索深さ上限 50）
    private readonly float[][]? _acc;   // [depth][L1_SIZE]
    private int _accDepth;

    public CαβAI(string? paramsPath = null, string? bookPath = null)
    {
        _p = αβパラメータ.Load(paramsPath);
        _定跡書 = C定跡書.Load(bookPath);

        var dir = paramsPath != null
            ? Path.GetDirectoryName(Path.GetFullPath(paramsPath)) ?? AppContext.BaseDirectory
            : AppContext.BaseDirectory;
        _nnue = CNNUE評価器.Load(Path.Combine(dir, "nnue_weights.bin"));
        if (_nnue != null)
        {
            _acc = new float[50][];
            for (int i = 0; i < 50; i++)
                _acc[i] = new float[CNNUE評価器.L1_SIZE];
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

    public S手? Get手(C盤面 盤面)
    {
        // 定跡書を優先参照（条件を満たす間のみ）
        if (_定跡書 != null)
        {
            var 定跡手 = _定跡書.QueryS手(盤面);
            if (定跡手.HasValue) return 定跡手.Value;
        }

        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        if (n == 0) return null;
        if (n == 1) return buf[0];

        // ルートアキュムレータを初期化（反復深化の都度リセット）
        if (_nnue != null && _acc != null)
        {
            _accDepth = 0;
            _nnue.ComputeAccum(盤面, _acc[0]);
        }

        // 反復深化：深さ 1 から _p.探索深さ まで順に探索
        S手 最善手 = buf[0];
        for (int 深さ = 1; 深さ <= _p.探索深さ; 深さ++)
        {
            _accDepth = 0;  // 各深さの探索開始前にリセット
            最善手 = SearchRoot(盤面, buf[..n], 深さ);
        }

        return 最善手;
    }

    public void Dispose() { }

    // ── ルートノード探索 ──────────────────────────────────────────────

    private S手 SearchRoot(C盤面 盤面, Span<S手> moves, int 深さ)
    {
        SortMoves(盤面, moves);

        S手 最善手 = moves[0];
        int alpha  = -詰みスコア;

        foreach (var 手 in moves)
        {
            // SearchRoot は Get合法手 済みなので全手合法
            var 指した側Root = 盤面.手番;
            var 取消 = 盤面.Apply(手);
            if (_acc != null)
            {
                E駒種? mk = 手.Is打ち ? null : (盤面.Get駒(手.Get移動先) is { } d
                    ? (手.Is成り ? C駒.Get成り前(d.種類) : d.種類) : (E駒種?)null);
                Array.Copy(_acc[_accDepth], _acc[_accDepth + 1], CNNUE評価器.L1_SIZE);
                _accDepth++;
                _nnue!.UpdateAccumAfterApply(_acc[_accDepth], 手, 指した側Root, mk, 取消.取り駒, 取消.中間取り駒);
            }
            int score = -Search(盤面, 深さ - 1, -詰みスコア, -alpha);
            盤面.Undo(手, 取消);
            if (_acc != null) _accDepth--;

            if (score > alpha)
            {
                alpha  = score;
                最善手 = 手;
            }
        }
        return 最善手;
    }

    // ── Negamax + αβ枝刈り ───────────────────────────────────────────
    // 内部ノードは Generate擬似合法手 + Apply後インライン王手放置チェックを使う。
    // これにより Get合法手（擬似生成+フィルタで Apply/Undo 2倍）を避け約2倍速になる。

    private int Search(C盤面 盤面, int 残深さ, int alpha, int beta)
    {
        if (残深さ <= 0)
        {
            return _acc != null
                ? _nnue!.EvaluateFromAccum(_acc[_accDepth])
                : (_nnue != null ? _nnue.Evaluate(盤面) : C評価関数.Evaluate(盤面, _p, _駒価値));
        }

        var 指した側 = 盤面.手番;

        Span<S手> moves = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Generate擬似合法手(盤面, moves);

        SortMoves(盤面, moves[..n]);

        bool 合法手あり = false;
        for (int i = 0; i < n; i++)
        {
            var 取消 = 盤面.Apply(moves[i]);

            if (!C合法手生成器.Is自玉安全(盤面, 指した側))
            {
                盤面.Undo(moves[i], 取消);
                continue;
            }

            // 合法手確定後にアキュムを更新
            if (_acc != null)
            {
                E駒種? mk = moves[i].Is打ち ? null : (盤面.Get駒(moves[i].Get移動先) is { } d
                    ? (moves[i].Is成り ? C駒.Get成り前(d.種類) : d.種類) : (E駒種?)null);
                Array.Copy(_acc[_accDepth], _acc[_accDepth + 1], CNNUE評価器.L1_SIZE);
                _accDepth++;
                _nnue!.UpdateAccumAfterApply(_acc[_accDepth], moves[i], 指した側, mk, 取消.取り駒, 取消.中間取り駒);
            }

            合法手あり = true;
            int score = -Search(盤面, 残深さ - 1, -beta, -alpha);
            盤面.Undo(moves[i], 取消);
            if (_acc != null) _accDepth--;

            if (score >= beta)  return beta;
            if (score > alpha)  alpha = score;
        }

        if (!合法手あり) return -詰みスコア;
        return alpha;
    }

    // ── 手順並べ替え（駒取り・成り優先）─────────────────────────────

    private void SortMoves(C盤面 盤面, Span<S手> moves)
    {
        Span<int> scores = stackalloc int[moves.Length];
        for (int i = 0; i < moves.Length; i++)
        {
            var 手 = moves[i];
            if (手.Is打ち) { scores[i] = 0; continue; }

            var 取り = 盤面.Get駒(手.Get移動先);
            scores[i] = 取り != null
                ? _駒価値[(int)取り.種類]       // MVV: 高価値の駒を取る手を優先
                : 手.Is成り ? 50 : 0;           // 次点：成り手
        }

        // 挿入ソート（~600手以下で十分高速）
        for (int i = 1; i < moves.Length; i++)
        {
            var h = moves[i];
            int s = scores[i];
            int j = i - 1;
            while (j >= 0 && scores[j] < s)
            {
                moves[j + 1]  = moves[j];
                scores[j + 1] = scores[j];
                j--;
            }
            moves[j + 1]  = h;
            scores[j + 1] = s;
        }
    }
}
