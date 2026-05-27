using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>
/// INT16/INT8 量子化 HalfKP NNUE 評価器。
///
/// ネットワーク構成:
///   特徴変換層 (L1): int16 加算器 × 2視点 → uint8[L1×2] (vpaddw, 2倍SIMD幅)
///   中間層     (L2): uint8[512] × int8[64×512] → float[64] (pmaddubsw, 4倍SIMD幅)
///   出力層     (L3): float[64] → float スカラー
///
/// 重みファイル形式 (NHKI = NNUE HalfKP Int):
///   Magic "NHKI" (4 bytes)
///   Q1: float32            (L1荷重量子化スケール)
///   L1_to_uint8: float32   (int16アキュム → uint8 変換係数)
///   W1_q[3][FS × L1]: int16
///   B1_q[3][L1]:      int16
///   Q2: float32            (L2荷重量子化スケール)
///   W2_q[L2 × L1×2]: int8 (出力優先: 各行=512バイト・32バイトアライン済み)
///   B2[L2]: float32
///   W3[L2]: float32
///   B3: float32
/// </summary>
public sealed unsafe class CNNUE評価器HalfKPInt8 : IDisposable
{
    // ── 定数 ─────────────────────────────────────────────────────────────────
    public const int 特徴量数  = 194_643;
    public const int L1数       = 256;
    public const int L2数       = 64;
    public const int 局面区分数  = 3;

    private readonly C特徴変換層Int8 _特徴変換層;
    private readonly C中間層Int8     _中間層;
    private readonly C出力層Int8     _出力層;

    // uint8 バッファ: [先手L1 | 後手L1] = 512バイト (16バイトアライン)
    private readonly nint _u8バッファ;

    private bool _disposed;

    private CNNUE評価器HalfKPInt8(C特徴変換層Int8 ft, C中間層Int8 l2, C出力層Int8 l3)
    {
        _特徴変換層 = ft;
        _中間層     = l2;
        _出力層     = l3;
        _u8バッファ = (nint)NativeMemory.AlignedAlloc((nuint)(L1数 * 2), 32);
    }

    // ── ロード ───────────────────────────────────────────────────────────────

    public static CNNUE評価器HalfKPInt8? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            var magic = br.ReadBytes(4);
            if (magic[0] != 'N' || magic[1] != 'H' || magic[2] != 'K' || magic[3] != 'I')
                return null;

            float Q1          = br.ReadSingle();
            float l1ToUint8   = br.ReadSingle();

            short[][] W1 = new short[局面区分数][];
            short[][] B1 = new short[局面区分数][];
            for (int bk = 0; bk < 局面区分数; bk++)
            {
                W1[bk] = ReadInt16(br, 特徴量数 * L1数);
                B1[bk] = ReadInt16(br, L1数);
            }

            float Q2         = br.ReadSingle();
            float dequantScale = 1f / (127f * Q2);
            var W2           = ReadInt8(br, L2数 * L1数 * 2);
            var B2           = ReadFloat(br, L2数);
            var W3           = ReadFloat(br, L2数);
            float B3         = br.ReadSingle();

            var ft = new C特徴変換層Int8(W1, B1);
            var l2 = new C中間層Int8(W2, B2, dequantScale);
            var l3 = new C出力層Int8(W3, B3, dequantScale);
            return new CNNUE評価器HalfKPInt8(ft, l2, l3);
        }
        catch { return null; }
    }

    // ── 局面区分 ─────────────────────────────────────────────────────────────

    public static int 局面区分番号取得(E駒種 自玉, E駒種 敵玉)
    {
        if (自玉 != E駒種.獅王 && 敵玉 != E駒種.獅王) return 0;
        if (自玉 != E駒種.獅王)                        return 1;
        return 2;
    }

    // ── 統計（デバッグ用） ────────────────────────────────────────────────────
    public long _stat_scratch;
    public long _stat_diff;

    // ── 加算器 API（CαβAI 向け）──────────────────────────────────────────────

    /// <summary>指定視点の L1 加算器を初期化（スクラッチ）。</summary>
    public void 加算器計算(C盤面 盤面, E手番 視点, int 局面区分, short[] acc)
        => _特徴変換層.加算器計算(盤面, 視点, 局面区分, acc);

    /// <summary>手を適用した後に加算器を差分更新（自玉移動時は全再計算に切り替え）。</summary>
    public void 加算器更新(
        C盤面 盤面, E手番 視点, int 旧局面区分, int 新局面区分,
        short[] acc, S手 手, S取消情報 取消)
    {
        if (旧局面区分 != 新局面区分 || 全再計算が必要(盤面, 手, 視点))
        {
            System.Threading.Interlocked.Increment(ref _stat_scratch);
            _特徴変換層.加算器計算(盤面, 視点, 新局面区分, acc);
        }
        else
        {
            System.Threading.Interlocked.Increment(ref _stat_diff);
            _特徴変換層.適用後加算器更新(盤面, 視点, 新局面区分, acc, 手, 取消);
        }
    }

    /// <summary>2視点の加算器から評価値（手番側視点 centipawn）を計算。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int 加算器から評価(short[] 先手L1, short[] 後手L1, E手番 手番)
    {
        byte* buf = (byte*)_u8バッファ;

        // 先手・後手それぞれ int16 → uint8 変換
        if (手番 == E手番.先手)
        {
            _特徴変換層.ToUint8(先手L1, buf);           // [0..255]: 先手(手番側)
            _特徴変換層.ToUint8(後手L1, buf + L1数);    // [256..511]: 後手
        }
        else
        {
            _特徴変換層.ToUint8(後手L1, buf);           // 後手が手番側
            _特徴変換層.ToUint8(先手L1, buf + L1数);
        }

        Span<short> L2出力 = stackalloc short[L2数];
        _中間層.Forward(buf, L2出力);
        return (int)(_出力層.Forward(L2出力) * 2000f);
    }

    // ── Dispose ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _特徴変換層.Dispose();
        _中間層.Dispose();
        if (_u8バッファ != 0) NativeMemory.AlignedFree((void*)_u8バッファ);
    }

    // ── プライベートヘルパー ──────────────────────────────────────────────────

    private static bool 全再計算が必要(C盤面 盤面, S手 手, E手番 視点)
    {
        if (手.Is打ち) return false;
        var 指した側    = 盤面.手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        var 移動後駒種  = 盤面.Get駒(手.Get移動先)!.種類;
        var 移動前駒種  = 手.Is成り ? C駒.Get成り前(移動後駒種) : 移動後駒種;
        return 指した側 == 視点
               && (移動前駒種 == E駒種.玉将 || 移動前駒種 == E駒種.獅王);
    }

    private static short[] ReadInt16(BinaryReader br, int n)
    {
        var buf = br.ReadBytes(n * 2);
        var arr = new short[n];
        Buffer.BlockCopy(buf, 0, arr, 0, buf.Length);
        return arr;
    }

    private static sbyte[] ReadInt8(BinaryReader br, int n)
    {
        var buf = br.ReadBytes(n);
        return MemoryMarshal.Cast<byte, sbyte>(buf).ToArray();
    }

    private static float[] ReadFloat(BinaryReader br, int n)
    {
        var buf = br.ReadBytes(n * 4);
        var arr = new float[n];
        Buffer.BlockCopy(buf, 0, arr, 0, buf.Length);
        return arr;
    }
}
