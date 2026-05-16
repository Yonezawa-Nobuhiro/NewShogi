using System.Text;

namespace 変成将棋.Models;

// 変成将棋用のFEN拡張表記（SFEN互換）を扱うユーティリティクラス。
// 標準SFENに加え、+G=鳳凰（金成）、+K=獅王（玉成）を追加定義している。
// 書式: <盤面> <手番(b/w)> <持ち駒> <手数>
// 盤面: 段1〜9を'/'区切り、各段は列9→1の順。大文字=先手、小文字=後手、数字=空升数
public static class C将棋FE表記
{
    // 記号→駒種。'+' 付きは成駒。大文字に統一してから参照する
    private static readonly Dictionary<string, E駒種> 記号駒種表 = new()
    {
        ["P"]  = E駒種.歩兵,  ["L"]  = E駒種.香車,  ["N"]  = E駒種.桂馬,
        ["S"]  = E駒種.銀将,  ["G"]  = E駒種.金将,  ["B"]  = E駒種.角行,
        ["R"]  = E駒種.飛車,  ["K"]  = E駒種.玉将,
        ["+P"] = E駒種.と金,  ["+L"] = E駒種.竪行,  ["+N"] = E駒種.騎兵,
        ["+S"] = E駒種.麒麟,  ["+G"] = E駒種.鳳凰,  ["+B"] = E駒種.龍馬,
        ["+R"] = E駒種.龍王,  ["+K"] = E駒種.獅王,
    };

    // 駒種→記号（記号駒種表の逆引き）
    private static readonly Dictionary<E駒種, string> 駒種記号表 =
        記号駒種表.ToDictionary(kv => kv.Value, kv => kv.Key);

    // SFEN文字列を解析して盤面に反映する
    public static void Setup(C盤面 盤面, string sfen)
    {
        var 部分 = sfen.Split(' ');

        Parse盤面(盤面, 部分[0]);

        if (部分.Length > 1)
            盤面.手番 = 部分[1] == "b" ? E手番.先手 : E手番.後手;

        if (部分.Length > 2 && 部分[2] != "-")
            Parse持ち駒(盤面, 部分[2]);
    }

    // 盤面文字列（'/'区切り9段分）を解析して升目に駒をセットする
    private static void Parse盤面(C盤面 盤面, string boardStr)
    {
        var 段一覧 = boardStr.Split('/');
        for (int 段 = 1; 段 <= 9; 段++)
        {
            int 列 = 9; // 左端（列9）から右端（列1）へ走査
            string row = 段一覧[段 - 1];
            int i = 0;
            while (i < row.Length)
            {
                char c = row[i];
                if (char.IsDigit(c))
                {
                    列 -= (c - '0'); // 数字は空升をスキップ
                    i++;
                }
                else
                {
                    bool 成り = c == '+';
                    if (成り) i++;
                    char 文字 = row[i];
                    string 記号 = (成り ? "+" : "") + char.ToUpper(文字);
                    E手番 手番 = char.IsUpper(文字) ? E手番.先手 : E手番.後手;

                    if (記号駒種表.TryGetValue(記号, out E駒種 種類))
                        盤面.Set駒(列, 段, 種類, 手番);

                    列--;
                    i++;
                }
            }
        }
    }

    // 持ち駒文字列を解析して盤面の持ち駒に反映する
    // 書式例: "2P3pS" → 先手歩2枚、後手歩3枚、先手銀1枚
    private static void Parse持ち駒(C盤面 盤面, string handStr)
    {
        int i = 0;
        while (i < handStr.Length)
        {
            int 枚数 = 0;
            while (i < handStr.Length && char.IsDigit(handStr[i]))
            {
                枚数 = 枚数 * 10 + (handStr[i] - '0');
                i++;
            }
            if (枚数 == 0) 枚数 = 1; // 枚数省略時は1枚

            if (i < handStr.Length)
            {
                char c = handStr[i];
                string 記号 = char.ToUpper(c).ToString();
                E手番 手番 = char.IsUpper(c) ? E手番.先手 : E手番.後手;

                if (記号駒種表.TryGetValue(記号, out E駒種 種類))
                {
                    var 持ち駒 = 手番 == E手番.先手 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;
                    持ち駒[種類] = 持ち駒.GetValueOrDefault(種類) + 枚数;
                }
                i++;
            }
        }
    }

    // 盤面をSFEN文字列にシリアライズする
    public static string Serialize(C盤面 盤面)
    {
        var sb = new StringBuilder();

        for (int 段 = 1; 段 <= 9; 段++)
        {
            if (段 > 1) sb.Append('/');
            int 空白数 = 0;
            for (int 列 = 9; 列 >= 1; 列--)
            {
                var 駒 = 盤面.Get升(列, 段).駒;
                if (駒 == null)
                {
                    空白数++;
                }
                else
                {
                    if (空白数 > 0) { sb.Append(空白数); 空白数 = 0; }
                    string 記号 = 駒種記号表[駒.種類];
                    if (記号.StartsWith('+'))
                        sb.Append('+').Append(駒.手番 == E手番.先手 ? char.ToUpper(記号[1]) : char.ToLower(記号[1]));
                    else
                        sb.Append(駒.手番 == E手番.先手 ? 記号.ToUpper() : 記号.ToLower());
                }
            }
            if (空白数 > 0) sb.Append(空白数);
        }

        sb.Append(盤面.手番 == E手番.先手 ? " b " : " w ");

        // 持ち駒（慣例の順序: 飛角金銀桂香歩）
        var handSb = new StringBuilder();
        Append持ち駒(handSb, 盤面.先手持ち駒, true);
        Append持ち駒(handSb, 盤面.後手持ち駒, false);
        sb.Append(handSb.Length > 0 ? handSb.ToString() : "-");

        sb.Append(" 1");
        return sb.ToString();
    }

    private static void Append持ち駒(StringBuilder sb, Dictionary<E駒種, int> 持ち駒, bool 先手)
    {
        E駒種[] 順序 = [E駒種.飛車, E駒種.角行, E駒種.金将, E駒種.銀将, E駒種.桂馬, E駒種.香車, E駒種.歩兵];
        foreach (var 種類 in 順序)
        {
            if (!持ち駒.TryGetValue(種類, out int 枚数) || 枚数 <= 0) continue;
            if (枚数 > 1) sb.Append(枚数);
            sb.Append(先手 ? 駒種記号表[種類].ToUpper() : 駒種記号表[種類].ToLower());
        }
    }
}
