using 変成将棋.Models;

namespace 変成将棋;

// Python.NET 経由で MCTS・学習パイプラインに公開する API。
// 局面は SFEN 文字列、手はポリシーインデックス（int）で交換する。
//
// ポリシーインデックス空間（合計 18,873）:
//   0       〜 13,121 : 盤上移動  from(81) × to(81) × promote(2)
//   13,122  〜 13,688 : 駒打ち    piece(7) × to(81)  ※歩=0,香=1,桂=2,銀=3,金=4,角=5,飛=6
//   13,689  〜 18,872 : 獅王2回移動  from(81) × via方向(8) × to方向(8)
//
// 盤面テンソル（length = 47 × 81 = 3,807, float32）:
//   ch  0-15 : 先手の各駒種 (E駒種 1-16)
//   ch 16-31 : 後手の各駒種 (E駒種 1-16)
//   ch 32-38 : 先手持ち駒枚数 (歩〜飛の7種, 全升にブロードキャスト)
//   ch 39-45 : 後手持ち駒枚数 (同上)
//   ch 46    : 現在手番 (先手=1, 後手=0)
//   フラット順: channel * 81 + (段-1) * 9 + (列-1)

public static class CPython連携
{
    // ===== 定数 =====

    public const int 手空間サイズ = 18_873;
    public const int テンソル長   = 47 * 81;

    private static readonly E駒種[] 持ち駒順 =
        [E駒種.歩兵, E駒種.香車, E駒種.桂馬, E駒種.銀将, E駒種.金将, E駒種.角行, E駒種.飛車];

    // ===== 公開 API =====

    /// <summary>初期局面 SFEN を返す。</summary>
    public static string 初期SFEN()
        => "lnsgkgsnl/1r5b1/ppppppppp/9/9/9/PPPPPPPPP/1B5R1/LNSGKGSNL b - 1";

    /// <summary>合法手のポリシーインデックス一覧を返す。</summary>
    public static int[] 合法手インデックス(string sfen)
    {
        var 盤面 = new C盤面(sfen);
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        var result = new int[n];
        for (int i = 0; i < n; i++)
            result[i] = ToIndex(buf[i]);
        return result;
    }

    /// <summary>手を適用して次の局面 SFEN を返す。</summary>
    public static string 手を適用(string sfen, int index)
    {
        var 盤面 = new C盤面(sfen);
        var 手 = FromIndex(index, 盤面);
        盤面.Apply(手);
        return 盤面.ToSFEN();
    }

    /// <summary>終局（合法手がない）かどうかを返す。</summary>
    public static bool Is終局(string sfen)
    {
        var 盤面 = new C盤面(sfen);
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        return C合法手生成器.Get合法手(盤面, buf) == 0;
    }

    /// <summary>1=先手勝ち, -1=後手勝ち, 0=進行中。</summary>
    public static int 結果(string sfen)
    {
        if (!Is終局(sfen)) return 0;
        var 盤面 = new C盤面(sfen);
        // 手番側が指せない = 手番側の負け
        return 盤面.手番 == E手番.先手 ? -1 : 1;
    }

    /// <summary>現在手番を "先手" / "後手" で返す。</summary>
    public static string 現在手番(string sfen)
    {
        var 盤面 = new C盤面(sfen);
        return 盤面.手番 == E手番.先手 ? "先手" : "後手";
    }

    /// <summary>S手 → ポリシーインデックス（C# の ONNX 推論用）。</summary>
    public static int 手インデックス(S手 手) => ToIndex(手);

    /// <summary>ポリシーインデックス → S手（NativeAOT 向けに公開）。</summary>
    public static S手 インデックスから手(int index, C盤面 盤面) => FromIndex(index, 盤面);

    /// <summary>盤面をニューラルネット入力テンソルに変換する（float[3807]）。</summary>
    public static float[] 盤面テンソル(string sfen) => 盤面テンソル(new C盤面(sfen));

    public static float[] 盤面テンソル(C盤面 盤面)
    {
        var tensor = new float[テンソル長];
        盤面テンソルへ書込(盤面, tensor);
        return tensor;
    }

    /// <summary>確保済みバッファへ直接書き込む（アロケーションゼロ版）。</summary>
    public static void 盤面テンソルへ書込(C盤面 盤面, Span<float> tensor)
    {
        tensor.Clear();

        // 盤上の駒（先手 ch0-15 / 後手 ch16-31）
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 駒 = 盤面.Get駒(列, 段);
                if (駒 == null) continue;
                int sq = (段 - 1) * 9 + (列 - 1);
                int base_ch = 駒.手番 == E手番.先手 ? 0 : 16;
                int ch = base_ch + (int)駒.種類 - 1;  // E駒種 1-16 → 0-15
                tensor[ch * 81 + sq] = 1f;
            }
        }

        // 持ち駒（先手 ch32-38 / 後手 ch39-45, 全升にブロードキャスト）
        for (int pi = 0; pi < 持ち駒順.Length; pi++)
        {
            float 先 = 盤面.先手持ち駒.GetValueOrDefault(持ち駒順[pi], 0);
            float 後 = 盤面.後手持ち駒.GetValueOrDefault(持ち駒順[pi], 0);
            int ch先 = 32 + pi;
            int ch後 = 39 + pi;
            for (int sq = 0; sq < 81; sq++)
            {
                tensor[ch先 * 81 + sq] = 先;
                tensor[ch後 * 81 + sq] = 後;
            }
        }

        // 現在手番（ch46）
        if (盤面.手番 == E手番.先手)
            for (int sq = 0; sq < 81; sq++)
                tensor[46 * 81 + sq] = 1f;
    }

    // ===== インデックス ⇔ S手 変換 =====

    private static int ToIndex(S手 手)
    {
        if (手.Is獅王2回移動)
        {
            int from = 手.Get移動元.線形インデックス;
            int via_dir = GetDir(手.Get移動元, 手.Get中間);
            int to_dir  = GetDir(手.Get中間,   手.Get移動先);
            return 13_689 + from * 64 + via_dir * 8 + to_dir;
        }
        if (手.Is打ち)
        {
            int piece = Array.IndexOf(持ち駒順, 手.Get打ち駒);
            int to = 手.Get移動先.線形インデックス;
            return 13_122 + piece * 81 + to;
        }
        {
            int from = 手.Get移動元.線形インデックス;
            int to   = 手.Get移動先.線形インデックス;
            int prom = 手.Is成り ? 1 : 0;
            return from * 162 + to * 2 + prom;
        }
    }

    private static S手 FromIndex(int index, C盤面 盤面)
    {
        // 生成済み合法手から対応する手を探す（インデックスの逆引き）
        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Get合法手(盤面, buf);
        for (int i = 0; i < n; i++)
            if (ToIndex(buf[i]) == index) return buf[i];
        throw new ArgumentException($"invalid move index: {index}");
    }

    private static int GetDir(S升座標 from, S升座標 to)
    {
        int d列 = to.列 - from.列;
        int d段 = to.段 - from.段;
        return (d列, d段) switch
        {
            ( 0, -1) => 0,
            ( 0, +1) => 1,
            (-1,  0) => 2,
            (+1,  0) => 3,
            (-1, -1) => 4,
            (+1, -1) => 5,
            (-1, +1) => 6,
            (+1, +1) => 7,
            _ => throw new ArgumentException($"not adjacent: d列={d列} d段={d段}")
        };
    }
}
