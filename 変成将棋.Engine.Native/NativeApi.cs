using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using 変成将棋.Models;

namespace 変成将棋.Native;

// NativeAOT でコンパイルされる C スタイル API。
// ctypes / cffi から呼び出すことで Python.NET ブリッジのオーバーヘッド (~8ms) を
// ~0.1ms に削減する。
//
// エクスポート関数一覧:
//   shogi_initial_sfen  → 初期局面 SFEN を書き込む
//   shogi_apply         → 手を適用して新 SFEN を書き込む（終局判定付き）
//   shogi_tensor        → 盤面テンソルを書き込む
//   shogi_legal_moves   → 合法手インデックスを書き込む
//   shogi_expand        → apply + tensor + legal_moves を 1 回で実行（バッチ API）
//   shogi_ownership     → 各升の帰属（KataGo 補助タスク）を書き込む
//
// 戻り値の符号規約:
//   >= 0 : 成功（合法手数など）
//   -1   : 終局（合法手なし）
//   -99  : 予期しないエラー

public static unsafe class NativeApi
{
    private const int SFEN_BUF  = 256;
    private const int MAX_MOVES = C合法手生成器.最大手数;
    private const int TENSOR_LEN = 47 * 81;

    // ===== 基本 API =====

    [UnmanagedCallersOnly(EntryPoint = "shogi_initial_sfen")]
    public static int InitialSfen(byte* buf, int bufLen)
    {
        try
        {
            return WriteStr(CPython連携.初期SFEN(), buf, bufLen);
        }
        catch { return -99; }
    }

    // apply + 終局チェックを 1 回で行う。
    // 戻り値: 0=継続, -1=終局(先手負け), 1=終局(後手負け), -99=エラー
    [UnmanagedCallersOnly(EntryPoint = "shogi_apply")]
    public static int Apply(byte* sfenIn, int moveIdx, byte* sfenOut, int bufLen)
    {
        try
        {
            var 盤面 = new C盤面(ReadStr(sfenIn));
            var 手   = CPython連携.インデックスから手(moveIdx, 盤面);
            盤面.Apply(手);
            WriteStr(盤面.ToSFEN(), sfenOut, bufLen);

            Span<S手> buf = stackalloc S手[MAX_MOVES];
            int n = C合法手生成器.Get合法手(盤面, buf);
            if (n > 0) return 0;                          // 継続
            return 盤面.手番 == E手番.先手 ? -1 : 1;      // 終局
        }
        catch { return -99; }
    }

    [UnmanagedCallersOnly(EntryPoint = "shogi_tensor")]
    public static int Tensor(byte* sfenIn, float* tensorOut)
    {
        try
        {
            var 盤面 = new C盤面(ReadStr(sfenIn));
            CPython連携.盤面テンソルへ書込(盤面, new Span<float>(tensorOut, TENSOR_LEN));
            return 0;
        }
        catch { return -99; }
    }

    // 戻り値: 合法手数, -1=終局, -99=エラー
    [UnmanagedCallersOnly(EntryPoint = "shogi_legal_moves")]
    public static int LegalMoves(byte* sfenIn, int* movesOut, int maxMoves)
    {
        try
        {
            var 盤面 = new C盤面(ReadStr(sfenIn));
            Span<S手> buf = stackalloc S手[MAX_MOVES];
            int n = C合法手生成器.Get合法手(盤面, buf);
            if (n == 0) return -1;
            int count = Math.Min(n, maxMoves);
            for (int i = 0; i < count; i++)
                movesOut[i] = CPython連携.手インデックス(buf[i]);
            return count;
        }
        catch { return -99; }
    }

    // ===== バッチ API（apply + tensor + legal_moves を 1 回で） =====

    // 戻り値: 合法手数(>0)=継続, -1=終局, -99=エラー
    // 終局時: sfenOut は書き込まれるが tensor/movesOut は未定義
    [UnmanagedCallersOnly(EntryPoint = "shogi_expand")]
    public static int Expand(
        byte* parentSfenIn, int moveIdx,
        byte* sfenOut, int sfenBufLen,
        float* tensorOut,
        int* movesOut, int maxMoves)
    {
        try
        {
            var 盤面 = new C盤面(ReadStr(parentSfenIn));
            var 手   = CPython連携.インデックスから手(moveIdx, 盤面);
            盤面.Apply(手);
            WriteStr(盤面.ToSFEN(), sfenOut, sfenBufLen);

            Span<S手> buf = stackalloc S手[MAX_MOVES];
            int n = C合法手生成器.Get合法手(盤面, buf);
            if (n == 0) return -1;   // 終局

            // テンソルを直接書き込み（アロケーションなし）
            CPython連携.盤面テンソルへ書込(盤面, new Span<float>(tensorOut, TENSOR_LEN));

            // 合法手を書き込み
            int count = Math.Min(n, maxMoves);
            for (int i = 0; i < count; i++)
                movesOut[i] = CPython連携.手インデックス(buf[i]);
            return count;
        }
        catch { return -99; }
    }

    // ===== KataGo 補助タスク =====

    // 各升の帰属（先手利き=+1, 後手利き=-1, 互角=0）を書き込む
    [UnmanagedCallersOnly(EntryPoint = "shogi_ownership")]
    public static unsafe int Ownership(byte* sfenIn, float* ownershipOut)
    {
        try
        {
            var 盤面 = new C盤面(ReadStr(sfenIn));
            for (int sq = 0; sq < 81; sq++)
            {
                int 段 = sq / 9 + 1;
                int 列 = sq % 9 + 1;
                var 升 = new S升座標((byte)列, (byte)段);
                bool 先 = 盤面.先手利き.Contains(升);
                bool 後 = 盤面.後手利き.Contains(升);
                ownershipOut[sq] = 先 && !後 ? 1f : !先 && 後 ? -1f : 0f;
            }
            return 0;
        }
        catch { return -99; }
    }

    // ===== 文字列ヘルパー =====

    private static string ReadStr(byte* ptr)
        => Marshal.PtrToStringUTF8((IntPtr)ptr) ?? "";

    private static int WriteStr(string s, byte* buf, int bufLen)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length + 1 > bufLen) return -99;
        bytes.AsSpan().CopyTo(new Span<byte>(buf, bufLen));
        buf[bytes.Length] = 0;
        return bytes.Length;
    }
}
