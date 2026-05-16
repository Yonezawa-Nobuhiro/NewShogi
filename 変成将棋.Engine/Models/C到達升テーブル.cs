namespace 変成将棋.Models;

// 各駒種の到達可能升を全升について事前計算したテーブル。
// 合法手生成で都度ベクトル計算する代わりにこのテーブルを参照する。
// 升の指定は S升座標.Byte値（ニブル表現）で行う。
public static class C到達升テーブル
{
    // スライド8方向の定数（先手視点）
    public const int 前  = 0; // (d列= 0, d段=-1)
    public const int 後  = 1; // (d列= 0, d段=+1)
    public const int 右  = 2; // (d列=-1, d段= 0)
    public const int 左  = 3; // (d列=+1, d段= 0)
    public const int 右前 = 4; // (d列=-1, d段=-1)
    public const int 左前 = 5; // (d列=+1, d段=-1)
    public const int 右後 = 6; // (d列=-1, d段=+1)
    public const int 左後 = 7; // (d列=+1, d段=+1)

    private static readonly (int d列, int d段)[] _方向ベクトル =
    [
        ( 0, -1), ( 0, +1), (-1,  0), (+1,  0),
        (-1, -1), (+1, -1), (-1, +1), (+1, +1),
    ];

    // [方向(0-7)][升Byte値] → その方向の升一覧（近い順）
    // [方向(0-7)][升Byte値] → その方向の升一覧（近い順）
    // 駒種ではなく方向単位で持つ理由：飛車と龍王は同じ前後左右を、角行と龍馬は同じ斜め4方向を
    // 共有するため、方向ごとに1本持てば重複なく済む。どの駒がどの方向を使うかは
    // C駒動き定数 で定義し、合法手生成器が参照する。
    private static readonly byte[][][] _スライドレイ;

    // [E駒種 as int][E手番 as int][升Byte値] → 非スライド到達升一覧
    // 純スライド駒（香車・角行・飛車）と獅王はnull（空span返却）
    private static readonly byte[][][][] _到達升;

    // 獅王専用
    private static readonly byte[][] _獅王隣接; // タイプA：隣接8升
    private static readonly byte[][] _獅王遠達; // タイプB：チェビシェフ距離2の升

    // ===== ビットボードテーブル（C利き管理の高速計算用） =====

    // [方向(0-7)][升Byte値] → その方向の全レイビットボード（盤面状態無視の最大利き）
    private static readonly S利きビット[][] _レイビット;

    // [E駒種 as int][E手番 as int][升Byte値] → 固定移動の利きビットボード
    private static readonly S利きビット[][][] _固定利きビット;

    // 獅王専用ビットボード
    private static readonly S利きビット[] _獅王隣接ビット;
    private static readonly S利きビット[] _獅王遠達ビット;

    static C到達升テーブル()
    {
        _スライドレイ    = BuildスライドレイAll();
        _到達升          = Build到達升All();
        _獅王隣接        = Build獅王隣接();
        _獅王遠達        = Build獅王遠達();
        _レイビット      = BuildレイビットAll();
        _固定利きビット  = Build固定利きビットAll();
        _獅王隣接ビット  = BuildビットFrom(_獅王隣接);
        _獅王遠達ビット  = BuildビットFrom(_獅王遠達);
    }

    // スライド駒用：指定方向の升一覧を取得
    public static ReadOnlySpan<byte> Getスライドレイ(int 方向, S升座標 升)
    {
        var 結果 = _スライドレイ[方向][升.Byte値];
        return 結果 ?? [];
    }

    // 非スライド駒用：到達可能升一覧を取得
    public static ReadOnlySpan<byte> Get到達升(E駒種 種類, E手番 手番, S升座標 升)
    {
        var 結果 = _到達升[(int)種類][(int)手番][升.Byte値];
        return 結果 ?? [];
    }

    // 獅王タイプA（隣接1〜2回移動）の1回目到達升
    public static ReadOnlySpan<byte> Get獅王隣接(S升座標 升)
    {
        var 結果 = _獅王隣接[升.Byte値];
        return 結果 ?? [];
    }

    // 獅王タイプB（チェビシェフ距離2）の到達升
    public static ReadOnlySpan<byte> Get獅王遠達(S升座標 升)
    {
        var 結果 = _獅王遠達[升.Byte値];
        return 結果 ?? [];
    }

    // ===== ビルダー =====

    private static byte[][][] BuildスライドレイAll()
    {
        var table = new byte[8][][];
        for (int dir = 0; dir < 8; dir++)
        {
            table[dir] = new byte[256][];
            var (d列, d段) = _方向ベクトル[dir];
            for (int 段 = 1; 段 <= 9; 段++)
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 元 = new S升座標((byte)列, (byte)段);
                var レイ = new List<byte>(8);
                var 現在 = 元.Add(d列, d段);
                while (現在.Is盤内)
                {
                    レイ.Add(現在.Byte値);
                    現在 = 現在.Add(d列, d段);
                }
                table[dir][元.Byte値] = レイ.ToArray();
            }
        }
        return table;
    }

    private static byte[][][][] Build到達升All()
    {
        int 駒種数 = (int)E駒種.獅王 + 1;
        var table = new byte[駒種数][][][];
        for (int i = 0; i < 駒種数; i++)
        {
            table[i] = new byte[2][][];
            for (int j = 0; j < 2; j++)
                table[i][j] = new byte[256][];
        }

        // 方向依存（先後別）
        Setup固定(table, E駒種.歩兵, E手番.先手, C駒動き定数.歩兵先手);
        Setup固定(table, E駒種.歩兵, E手番.後手, C駒動き定数.歩兵後手);

        Setup固定(table, E駒種.桂馬, E手番.先手, C駒動き定数.桂馬先手);
        Setup固定(table, E駒種.桂馬, E手番.後手, C駒動き定数.桂馬後手);

        Setup固定(table, E駒種.銀将, E手番.先手, C駒動き定数.銀将先手);
        Setup固定(table, E駒種.銀将, E手番.後手, C駒動き定数.銀将後手);

        Setup固定(table, E駒種.金将, E手番.先手, C駒動き定数.金将先手);
        Setup固定(table, E駒種.金将, E手番.後手, C駒動き定数.金将後手);
        Setup固定(table, E駒種.と金, E手番.先手, C駒動き定数.金将先手);
        Setup固定(table, E駒種.と金, E手番.後手, C駒動き定数.金将後手);

        // 先後対称（先手テーブルを後手と共有）
        Setup固定両手番(table, E駒種.玉将,  C駒動き定数.玉将);
        Setup固定両手番(table, E駒種.竪行,  C駒動き定数.竪行横);
        Setup固定両手番(table, E駒種.騎兵,  C駒動き定数.騎兵);
        Setup固定両手番(table, E駒種.麒麟,  C駒動き定数.麒麟);
        Setup固定両手番(table, E駒種.鳳凰,  C駒動き定数.鳳凰);
        Setup固定両手番(table, E駒種.龍馬,  C駒動き定数.龍馬縦横);
        Setup固定両手番(table, E駒種.龍王,  C駒動き定数.龍王斜め);

        // 香車・角行・飛車・獅王はnullのまま（スライドのみ or 別テーブル）

        return table;
    }

    private static void Setup固定(
        byte[][][][] table, E駒種 種類, E手番 手番, (int d列, int d段)[] Δ)
    {
        int ki = (int)種類, ti = (int)手番;
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 元 = new S升座標((byte)列, (byte)段);
                List<byte> 到達 = new(Δ.Length);
                foreach (var (d列, d段) in Δ)
                {
                    var 先 = 元.Add(d列, d段);
                    if (先.Is盤内) 到達.Add(先.Byte値);
                }
                table[ki][ti][元.Byte値] = 到達.ToArray();
            }
        }
    }

    // 先後対称な駒：先手テーブルを構築し後手は参照を共有
    private static void Setup固定両手番(byte[][][][] table, E駒種 種類, (int d列, int d段)[] Δ)
    {
        Setup固定(table, 種類, E手番.先手, Δ);
        int ki = (int)種類;
        for (int k = 0; k < 256; k++)
            table[ki][(int)E手番.後手][k] = table[ki][(int)E手番.先手][k];
    }

    private static byte[][] Build獅王隣接()
    {
        var table = new byte[256][];
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 元 = new S升座標((byte)列, (byte)段);
                List<byte> 隣接 = new(8);
                foreach (var (d列, d段) in _方向ベクトル)
                {
                    var 先 = 元.Add(d列, d段);
                    if (先.Is盤内) 隣接.Add(先.Byte値);
                }
                table[元.Byte値] = [..隣接];
            }
        }
        return table;
    }

    private static byte[][] Build獅王遠達()
    {
        var table = new byte[256][];
        // max(|d列|, |d段|) == 2 の全16方向
        var Δ = new List<(int, int)>(16);
        for (int d列 = -2; d列 <= 2; d列++)
        for (int d段 = -2; d段 <= 2; d段++)
            if (Math.Max(Math.Abs(d列), Math.Abs(d段)) == 2)
                Δ.Add((d列, d段));

        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 元 = new S升座標((byte)列, (byte)段);
            var 遠達 = new List<byte>(16);
            foreach (var (d列, d段) in Δ)
            {
                var 先 = 元.Add(d列, d段);
                if (先.Is盤内) 遠達.Add(先.Byte値);
            }
            table[元.Byte値] = 遠達.ToArray();
        }
        return table;
    }

    // ===== ビットボードテーブルのアクセサ =====

    // 指定方向の全レイビットボード（ブロック考慮なし）
    public static S利きビット Getレイビット(int 方向, S升座標 升)
        => _レイビット[方向][升.Byte値];

    // 固定移動の利きビットボード（O(1)）
    public static S利きビット Get固定利きビット(E駒種 種類, E手番 手番, S升座標 升)
        => _固定利きビット[(int)種類][(int)手番][升.Byte値];

    // 獅王専用ビットボード
    public static S利きビット Get獅王隣接ビット(S升座標 升) => _獅王隣接ビット[升.Byte値];
    public static S利きビット Get獅王遠達ビット(S升座標 升) => _獅王遠達ビット[升.Byte値];

    // ===== ビットボードテーブルのビルダー =====

    // 方向レイのビットボードテーブルを構築（既存byte[]テーブルから変換）
    private static S利きビット[][] BuildレイビットAll()
    {
        var table = new S利きビット[8][];
        for (int dir = 0; dir < 8; dir++)
        {
            table[dir] = new S利きビット[256];
            for (int 段 = 1; 段 <= 9; 段++)
            {
                for (int 列 = 1; 列 <= 9; 列++)
                {
                    var 元 = new S升座標((byte)列, (byte)段);
                    var bits = S利きビット.空;
                    foreach (byte rb in Getスライドレイ(dir, 元))
                        bits = bits.Set(new S升座標(rb));
                    table[dir][元.Byte値] = bits;
                }
            }
        }
        return table;
    }

    // 固定移動利きのビットボードテーブルを構築（既存byte[]テーブルから変換）
    private static S利きビット[][][] Build固定利きビットAll()
    {
        const int 駒種数 = (int)E駒種.獅王 + 1;
        var table = new S利きビット[駒種数][][];
        for (int i = 0; i < 駒種数; i++)
        {
            table[i] = new S利きビット[2][];
            for (int j = 0; j < 2; j++)
            {
                table[i][j] = new S利きビット[256];
                for (int 段 = 1; 段 <= 9; 段++)
                {
                    for (int 列 = 1; 列 <= 9; 列++)
                    {
                        var 元 = new S升座標((byte)列, (byte)段);
                        var bits = S利きビット.空;
                        foreach (byte rb in Get到達升((E駒種)i, (E手番)j, 元))
                            bits = bits.Set(new S升座標(rb));
                        table[i][j][元.Byte値] = bits;
                    }
                }
            }
        }
        return table;
    }

    // byte[][]テーブルからS利きビット[]テーブルを構築する汎用ヘルパー
    private static S利きビット[] BuildビットFrom(byte[][] byteTable)
    {
        var table = new S利きビット[256];
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 元 = new S升座標((byte)列, (byte)段);
                var bits = S利きビット.空;
                var arr = byteTable[元.Byte値];
                if (arr != null)
                {
                    foreach (byte rb in arr)
                        bits = bits.Set(new S升座標(rb));
                }
                table[元.Byte値] = bits;
            }
        }
        return table;
    }
}
