// Python.NET から import できるよう ASCII 名前空間でラップするブリッジクラス。
// Python 側: from ShogiBridge import Api
namespace ShogiBridge
{
    public static class Api
    {
        public const int ACTION_SIZE    = 変成将棋.CPython連携.手空間サイズ;
        public const int TENSOR_LENGTH  = 変成将棋.CPython連携.テンソル長;

        public static string   initial_sfen()                    => 変成将棋.CPython連携.初期SFEN();
        public static int[]    legal_moves(string sfen)          => 変成将棋.CPython連携.合法手インデックス(sfen);
        public static string   apply_move(string sfen, int idx)  => 変成将棋.CPython連携.手を適用(sfen, idx);
        public static bool     is_terminal(string sfen)          => 変成将棋.CPython連携.Is終局(sfen);
        public static int      result(string sfen)               => 変成将棋.CPython連携.結果(sfen);
        public static string   current_player(string sfen)       => 変成将棋.CPython連携.現在手番(sfen);
        public static float[]  board_tensor(string sfen)         => 変成将棋.CPython連携.盤面テンソル(sfen);
    }
}
