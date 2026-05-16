using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 変成将棋.Models;

public class C局面設定
{
    [JsonPropertyName("開始局面")]
    public string 開始局面 { get; set; } = "lnsgkgsnl/1r5b1/ppppppppp/9/9/9/PPPPPPPPP/1B5R1/LNSGKGSNL b - 1";

    public static C局面設定 Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "局面設定.json");
        if (!File.Exists(path)) return new C局面設定();
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<C局面設定>(json) ?? new C局面設定();
    }
}
