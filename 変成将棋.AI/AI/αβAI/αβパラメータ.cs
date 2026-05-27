using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 変成将棋.AI.αβAI;

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
    [JsonPropertyName("詰み探索手数")]     public int 詰み探索手数     { get; init; } = 5;
    [JsonPropertyName("Quiesce深さ")]      public int Quiesce深さ      { get; init; } = 4;

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
