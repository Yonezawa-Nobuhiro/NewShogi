namespace 変成将棋.Models;

// 各駒種の移動デルタ（d列, d段）を定義する定数クラス。
// C到達升テーブルのビルダーから参照する。
public static class C駒動き定数
{
    public static readonly (int d列, int d段)[] 歩兵先手 = [(0, -1)];
    public static readonly (int d列, int d段)[] 歩兵後手 = [(0, +1)];

    public static readonly (int d列, int d段)[] 桂馬先手 = [(-1, -2), (+1, -2)];
    public static readonly (int d列, int d段)[] 桂馬後手 = [(-1, +2), (+1, +2)];

    public static readonly (int d列, int d段)[] 銀将先手 = [(-1, -1), (0, -1), (+1, -1), (-1, +1), (+1, +1)];
    public static readonly (int d列, int d段)[] 銀将後手 = [(-1, -1), (+1, -1), (-1, +1), (+1, +1), (0, +1)];

    public static readonly (int d列, int d段)[] 金将先手 = [(-1, -1), (0, -1), (+1, -1), (-1, 0), (+1, 0), (0, +1)];
    public static readonly (int d列, int d段)[] 金将後手 = [(-1, +1), (0, +1), (+1, +1), (-1, 0), (+1, 0), (0, -1)];

    // 先後対称（180°回転で不変）
    public static readonly (int d列, int d段)[] 玉将 =
        [(-1, -1), (0, -1), (+1, -1), (-1, 0), (+1, 0), (-1, +1), (0, +1), (+1, +1)];

    public static readonly (int d列, int d段)[] 竪行横 = [(-1, 0), (+1, 0)];

    public static readonly (int d列, int d段)[] 騎兵 =
        [(-1, -2), (+1, -2), (-2, -1), (+2, -1), (-2, +1), (+2, +1), (-1, +2), (+1, +2)];

    public static readonly (int d列, int d段)[] 麒麟 =
        [(-1, -1), (+1, -1), (-1, +1), (+1, +1), (0, -2), (0, +2), (-2, 0), (+2, 0)];

    public static readonly (int d列, int d段)[] 鳳凰 =
        [(0, -1), (0, +1), (-1, 0), (+1, 0), (-2, -2), (+2, -2), (-2, +2), (+2, +2)];

    public static readonly (int d列, int d段)[] 龍馬縦横 = [(0, -1), (0, +1), (-1, 0), (+1, 0)];

    public static readonly (int d列, int d段)[] 龍王斜め = [(-1, -1), (+1, -1), (-1, +1), (+1, +1)];

    // 走り駒のスライド方向（C到達升テーブルの方向定数を使用）
    public static readonly int[] 香車先手スライド = [C到達升テーブル.前];
    public static readonly int[] 香車後手スライド = [C到達升テーブル.後];
    public static readonly int[] 角行スライド     = [C到達升テーブル.右前, C到達升テーブル.左前, C到達升テーブル.右後, C到達升テーブル.左後];
    public static readonly int[] 飛車スライド     = [C到達升テーブル.前, C到達升テーブル.後, C到達升テーブル.右, C到達升テーブル.左];
    public static readonly int[] 竪行スライド     = [C到達升テーブル.前, C到達升テーブル.後];
    // 龍馬・龍王のスライド部分は角行・飛車と同じ方向を共有
}
