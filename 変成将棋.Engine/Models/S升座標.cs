namespace 変成将棋.Models;

// 升目の座標を1バイトで表す値型。
// 上位4ビット=段（1〜9）、下位4ビット=列（1〜9）、0x00=無効。
// 9×9=81 < 256 のためbyteに収まる。16進数表示で段列が直読できる（例: 0x37=3段7列）。
public readonly struct S升座標
{
    private readonly byte _値;

    public byte 列 => (byte)(_値 & 0x0F);
    public byte 段 => (byte)(_値 >> 4);
    public bool Is有効 => _値 != 0;
    public bool Is盤内 => 列 >= 1 && 列 <= 9 && 段 >= 1 && 段 <= 9;

    public static readonly S升座標 なし = new(0);

    public S升座標(byte 列, byte 段) => _値 = (byte)(段 << 4 | 列);
    internal S升座標(byte 値) => _値 = 値; // テーブルのbyte値から復元するため

    // ベクトル加算（合法手生成で移動先を求めるときに使う）
    public S升座標 Add(int d列, int d段)
    {
        int 新列 = 列 + d列;
        int 新段 = 段 + d段;
        if (新列 < 1 || 新列 > 9 || 新段 < 1 || 新段 > 9) return なし;
        return new S升座標((byte)新列, (byte)新段);
    }

    internal byte Byte値 => _値; // テーブル参照用

    // ビットボード用線形インデックス (段-1)*9 + (列-1) → 0〜80
    public int 線形インデックス => ((段 - 1) * 9) + (列 - 1);

    // 線形インデックスから升座標に変換（ビットイテレーション用）
    public static S升座標 FromLinear(int idx) => new((byte)(idx % 9 + 1), (byte)(idx / 9 + 1));

    public bool Equals(S升座標 other) => _値 == other._値;
    public override string ToString() => Is有効 ? $"{段}段{列}列" : "なし";
}
