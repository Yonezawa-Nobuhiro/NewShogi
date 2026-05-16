namespace 変成将棋.Models;

// 指し手を表す4バイトの値型。
// byte×4フィールドで構成し、GCフリー・キャッシュ効率を最大化する。
// 64バイトキャッシュラインに16手がぴったり収まる。
// stackalloc S手[600] でスタック上に手一覧を確保することを想定している。
public readonly struct S手
{
    public readonly byte 移動元;   // S升座標（ニブル）、0=駒打ち
    public readonly byte 移動先;   // S升座標（ニブル）
    public readonly byte 中間;     // S升座標（ニブル）、0=獅王タイプA 2回移動以外
    public readonly byte 手フラグ;  // bit7=成り、bit6-0=打ち駒（E駒種値）

    public S升座標 Get移動元 => new((byte)(移動元 & 0x0F), (byte)(移動元 >> 4));
    public S升座標 Get移動先 => new((byte)(移動先 & 0x0F), (byte)(移動先 >> 4));
    public S升座標 Get中間   => new((byte)(中間   & 0x0F), (byte)(中間   >> 4));

    public bool    Is打ち        => 移動元 == 0;
    public bool    Is獅王2回移動 => 中間 != 0;
    public bool    Is成り        => (手フラグ & 0x80) != 0;
    public E駒種   Get打ち駒     => (E駒種)(手フラグ & 0x7F);

    private S手(byte 移動元, byte 移動先, byte 中間, byte 手フラグ)
    {
        this.移動元  = 移動元;
        this.移動先  = 移動先;
        this.中間    = 中間;
        this.手フラグ = 手フラグ;
    }

    private static byte ToRaw(S升座標 座標) => (byte)(座標.段 << 4 | 座標.列);

    // 通常手
    public static S手 Create通常(S升座標 移動元, S升座標 移動先, bool 成り = false)
        => new(ToRaw(移動元), ToRaw(移動先), 0, 成り ? (byte)0x80 : (byte)0);

    // 駒打ち
    public static S手 Create打ち(E駒種 駒種, S升座標 移動先)
        => new(0, ToRaw(移動先), 0, (byte)駒種);

    // 獅王タイプA 2回移動
    public static S手 Create獅王2回移動(S升座標 移動元, S升座標 中間, S升座標 移動先)
        => new(ToRaw(移動元), ToRaw(移動先), ToRaw(中間), 0);
}
