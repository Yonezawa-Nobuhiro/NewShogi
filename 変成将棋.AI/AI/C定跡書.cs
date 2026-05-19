using System.IO;
using 変成将棋.Models;

namespace 変成将棋.AI;

/// <summary>
/// やねうら王 standard_book.db 形式の定跡ファイルを読み込み、局面に対して推奨手を返す。
///
/// フォーマット（テキスト .db）:
///   sfen &lt;SFEN手数なし&gt;
///   &lt;USI手&gt; &lt;評価値&gt; &lt;深さ&gt; &lt;出現数&gt;
///   ...（複数行）
///
/// 使用条件:
///   - 盤面.手数 &lt;= 最大手数（デフォルト20）
///   - 変成将棋固有の成り駒（竪行・騎兵・麒麟・鳳凰・獅王）が盤上に存在しない
/// </summary>
public sealed class C定跡書
{
    // SFEN(手数フィールドなし) → (USI手, 重み, 生スコア) リスト
    private readonly Dictionary<string, List<(string usi, int rank, int score)>> _table = new();

    private static readonly Random _rng = new();

    // 変成将棋固有の成り駒（標準将棋に存在しない or 動きが異なる）
    private static readonly E駒種[] 非標準成駒 =
        [E駒種.竪行, E駒種.騎兵, E駒種.麒麟, E駒種.鳳凰, E駒種.獅王];

    public int 最大手数 { get; init; } = 20;

    // ── ロード ───────────────────────────────────────────────────────

    public static C定跡書? Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "変成将棋_定跡書.db");
        if (!File.Exists(path)) return null;

        var book = new C定跡書();
        book.LoadDB(path);
        Console.WriteLine($"定跡書ロード: {book._table.Count:N0} 局面 ← {path}");
        return book;
    }

    private void LoadDB(string path)
    {
        string? currentSfen = null;
        foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
        {
            if (line.StartsWith("sfen "))
            {
                currentSfen = Normalize(line.Substring(5).Trim());
                _table[currentSfen] = [];
            }
            else if (currentSfen != null && line.Length >= 4 && !line.StartsWith('#'))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                string usi = parts[0];

                // DB2016形式: "move none score depth count"
                // 旧形式:     "move score depth count"
                bool isDB2016 = parts.Length >= 2 && parts[1] == "none";
                int scoreIdx  = isDB2016 ? 2 : 1;
                int countIdx  = isDB2016 ? 4 : 3;

                int score = scoreIdx < parts.Length && int.TryParse(parts[scoreIdx], out int s) ? s : 0;
                int count = countIdx < parts.Length && int.TryParse(parts[countIdx], out int c) ? c : 0;

                // count=0 の場合は score で代用（DB2016 は count が 0 のことが多い）
                int rank = count > 0 ? count : Math.Max(0, score);
                _table[currentSfen].Add((usi, rank, score));
            }
        }
    }

    // ── 検索 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 現局面で定跡手があれば返す。使用条件を満たさない場合は null。
    /// </summary>
    public S手? QueryS手(C盤面 盤面)
    {
        // 使用条件チェック
        if (盤面.手数 > 最大手数) return null;
        if (Has非標準成駒(盤面)) return null;

        // 定跡テーブル検索
        // 定跡書はすべて先手番(b)で正規化されているため、後手番の場合は盤面を反転して検索する
        bool is後手 = 盤面.手番 == E手番.後手;
        var norm = Normalize(盤面.ToSFEN());
        var key = is後手 ? FlipSFEN(norm) : norm;
        if (!_table.TryGetValue(key, out var moves) || moves.Count == 0) return null;

        // rank > 0 の手を rank 比例で選択。
        // 全 rank=0（スコアが全て負）の場合は生スコアの相対値で重み付け。
        int totalRank = 0;
        foreach (var m in moves) totalRank += m.rank;

        string selectedUsi;
        if (totalRank > 0)
        {
            // rank 比例でランダム選択（スコア負の手は除外）
            int r = _rng.Next(totalRank);
            selectedUsi = moves.Last(m => m.rank > 0).usi;
            foreach (var m in moves)
            {
                if (m.rank <= 0) continue;
                r -= m.rank;
                if (r < 0) { selectedUsi = m.usi; break; }
            }
        }
        else
        {
            // 全スコア負 → 最善手から Threshold 点以内の手だけ相対重み付き選択
            const int Threshold = 50;
            int maxScore = moves.Max(m => m.score);
            int cutoff   = maxScore - Threshold;   // これ未満は除外

            int totalW = 0;
            foreach (var m in moves)
            {
                int w = m.score - cutoff;
                if (w > 0) totalW += w;
            }

            if (totalW == 0)
            {
                selectedUsi = moves[_rng.Next(moves.Count)].usi;
            }
            else
            {
                int r = _rng.Next(totalW);
                selectedUsi = moves[^1].usi;
                foreach (var m in moves)
                {
                    int w = m.score - cutoff;
                    if (w <= 0) continue;
                    r -= w;
                    if (r < 0) { selectedUsi = m.usi; break; }
                }
            }
        }

        // 後手番の場合は手を元の向きに戻す
        if (is後手) selectedUsi = FlipUSIMove(selectedUsi);

        // USI手 → S手 変換 → 合法手確認
        var 手 = ParseUSI(selectedUsi);
        if (手 == null) return null;

        return Is合法手(盤面, 手.Value) ? 手 : null;
    }

    // ── 内部ヘルパー ─────────────────────────────────────────────────

    private static bool Has非標準成駒(C盤面 盤面)
    {
        foreach (var 種類 in 非標準成駒)
        {
            if (!盤面.Get駒ビット(E手番.先手, 種類).IsEmpty) return true;
            if (!盤面.Get駒ビット(E手番.後手, 種類).IsEmpty) return true;
        }
        return false;
    }

    // 合法手リストに含まれるか確認
    private static bool Is合法手(C盤面 盤面, S手 手)
    {
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        for (int i = 0; i < n; i++)
        {
            var t = buf[i];
            if (t.移動元 == 手.移動元 &&
                t.移動先 == 手.移動先 &&
                t.手フラグ == 手.手フラグ) return true;
        }
        return false;
    }

    // USI形式の手を S手 に変換
    // 例: "7g7f"=普通手, "7g7f+"=成り, "P*3d"=打ち
    private static S手? ParseUSI(string usi)
    {
        if (usi.Length < 4) return null;

        if (usi[1] == '*')
        {
            // 打ち手: "P*3d"
            var 駒種 = USI駒(usi[0]);
            if (駒種 == null) return null;
            var 先 = USI升(usi[2], usi[3]);
            if (!先.Is有効) return null;
            return S手.Create打ち(駒種.Value, 先);
        }
        else
        {
            // 移動手: "7g7f" or "7g7f+"
            var 元 = USI升(usi[0], usi[1]);
            var 先 = USI升(usi[2], usi[3]);
            bool 成り = usi.Length >= 5 && usi[4] == '+';
            if (!元.Is有効 || !先.Is有効) return null;
            return S手.Create通常(元, 先, 成り);
        }
    }

    // USI 升目表記 → S升座標 (列=1-9, 段a=1..i=9)
    private static S升座標 USI升(char col, char rank)
    {
        if (col < '1' || col > '9') return S升座標.なし;
        int 列 = col - '0';
        int 段 = rank - 'a' + 1;
        if (段 < 1 || 段 > 9) return S升座標.なし;
        return new S升座標((byte)列, (byte)段);
    }

    // USI 駒文字 → E駒種
    private static E駒種? USI駒(char c) => char.ToUpper(c) switch
    {
        'P' => E駒種.歩兵,
        'L' => E駒種.香車,
        'N' => E駒種.桂馬,
        'S' => E駒種.銀将,
        'G' => E駒種.金将,
        'B' => E駒種.角行,
        'R' => E駒種.飛車,
        _   => null,
    };

    // SFEN 正規化：手数フィールド（末尾の数字）を除いた3フィールド
    private static string Normalize(string sfen)
    {
        var parts = sfen.Split(' ');
        return parts.Length >= 3 ? $"{parts[0]} {parts[1]} {parts[2]}" : sfen;
    }

    // ── 後手番 SFEN 正規化 ────────────────────────────────────────────
    // 定跡書は全局面を先手番(b)で保存。後手番局面は盤面180度回転+色反転で変換する。

    private static string FlipSFEN(string sfen)
    {
        var parts = sfen.Split(' ');
        string hands = parts.Length >= 3 ? parts[2] : "-";
        return $"{FlipBoard(parts[0])} b {FlipHands(hands)}";
    }

    private static string FlipBoard(string board)
    {
        var ranks = board.Split('/');
        Array.Reverse(ranks);
        return string.Join("/", ranks.Select(FlipRank));
    }

    private static string FlipRank(string rank)
    {
        var squares = new List<(bool promoted, char piece)>();
        int i = 0;
        while (i < rank.Length)
        {
            if (rank[i] == '+' && i + 1 < rank.Length)
            {
                squares.Add((true, rank[i + 1]));
                i += 2;
            }
            else if (char.IsDigit(rank[i]))
            {
                int n = rank[i] - '0';
                for (int j = 0; j < n; j++) squares.Add((false, '.'));
                i++;
            }
            else
            {
                squares.Add((false, rank[i]));
                i++;
            }
        }

        squares.Reverse();

        var sb = new System.Text.StringBuilder();
        int empty = 0;
        foreach (var (promoted, piece) in squares)
        {
            if (piece == '.')
            {
                empty++;
            }
            else
            {
                if (empty > 0) { sb.Append(empty); empty = 0; }
                if (promoted) sb.Append('+');
                sb.Append(char.IsUpper(piece) ? char.ToLower(piece) : char.ToUpper(piece));
            }
        }
        if (empty > 0) sb.Append(empty);
        return sb.ToString();
    }

    private static string FlipHands(string hands)
    {
        if (hands == "-") return "-";
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < hands.Length)
        {
            while (i < hands.Length && char.IsDigit(hands[i])) sb.Append(hands[i++]);
            if (i >= hands.Length) break;
            char c = hands[i++];
            sb.Append(char.IsUpper(c) ? char.ToLower(c) : char.ToUpper(c));
        }
        return sb.Length == 0 ? "-" : sb.ToString();
    }

    // 正規化（先手視点）のUSI手を後手視点に戻す（列・段を反転）
    private static string FlipUSIMove(string usi)
    {
        if (usi.Length < 4) return usi;

        if (usi[1] == '*')
        {
            // 打ち手: "P*3d" → 駒色反転 + 升目反転
            char piece = char.IsUpper(usi[0]) ? char.ToLower(usi[0]) : char.ToUpper(usi[0]);
            char col  = (char)('0' + 10 - (usi[2] - '0'));
            char rank = (char)('i' - (usi[3] - 'a'));
            return $"{piece}*{col}{rank}";
        }
        else
        {
            char fc = (char)('0' + 10 - (usi[0] - '0'));
            char fr = (char)('i' - (usi[1] - 'a'));
            char tc = (char)('0' + 10 - (usi[2] - '0'));
            char tr = (char)('i' - (usi[3] - 'a'));
            bool prom = usi.Length >= 5 && usi[4] == '+';
            return prom ? $"{fc}{fr}{tc}{tr}+" : $"{fc}{fr}{tc}{tr}";
        }
    }
}
