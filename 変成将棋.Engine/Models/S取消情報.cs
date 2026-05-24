namespace 変成将棋.Models;

// Apply後にUndoするために必要な情報。
// Apply前の盤面のうち、手の情報だけでは復元できない「取られた駒」を保持する。
public readonly struct S取消情報
{
    public readonly C駒 取り駒;     // 移動先で取った駒（E駒種.なし = なし）
    public readonly C駒 中間取り駒; // 獅王2回移動の中間升で取った駒（E駒種.なし = なし）

    public S取消情報(C駒 取り駒, C駒 中間取り駒 = default)
    {
        this.取り駒   = 取り駒;
        this.中間取り駒 = 中間取り駒;
    }
}
