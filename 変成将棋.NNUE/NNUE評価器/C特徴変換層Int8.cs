using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>
/// INT16 量子化特徴変換層（HalfKP）。
///
/// float 版との違い:
///   - 荷重: float[] → short[] (int16, NativeMemory 32バイトアライン)
///   - 加算器: float[] → short[] (int16)
///   - 更新: Vector256&lt;float&gt;(8要素) → Vector256&lt;short&gt;(16要素) = vpaddw で 2倍幅
///
/// 加算器スタックの所有・管理は呼び出し元（CNNUE評価器HalfKPInt8）が行う。
/// </summary>
internal sealed unsafe class C特徴変換層Int8 : IDisposable
{
    // ── 定数 ─────────────────────────────────────────────────────────────────
    internal const int L1数 = CNNUE評価器HalfKPInt8.L1数;  // 256
    private  const int FS  = CNNUE評価器HalfKPInt8.特徴量数;
    private  const int BK  = CNNUE評価器HalfKPInt8.局面区分数;

    private const int 持駒歩区分数 = 7;
    private const int 駒位置_開始   = 0;
    private const int 敵玉位置_開始 = 183_708;
    private const int 持駒歩_開始   = 190_269;
    private const int 持駒小駒_開始 = 191_403;
    private const int 持駒大駒_開始 = 193_995;

    private static readonly E駒種[] 小駒一覧 = [E駒種.香車, E駒種.桂馬, E駒種.銀将, E駒種.金将];
    private static readonly E駒種[] 大駒一覧 = [E駒種.角行, E駒種.飛車];

    // ── フィールド ────────────────────────────────────────────────────────────
    // NativeMemory で 32バイトアライン確保。各バケットの荷重行は
    // 512バイト (256 shorts × 2) 境界 → LoadAlignedVector256 使用可。
    private readonly nint[] _w1;  // [BK] → short*(FS × L1数), aligned
    private readonly nint[] _b1;  // [BK] → short*(L1数), aligned

    private bool _disposed;

    // ── 構築 ─────────────────────────────────────────────────────────────────

    internal C特徴変換層Int8(short[][] w1, short[][] b1)
    {
        _w1 = new nint[BK];
        _b1 = new nint[BK];
        for (int bk = 0; bk < BK; bk++)
        {
            uint w1sz = (uint)(FS * L1数 * sizeof(short));
            var pw1 = (short*)NativeMemory.AlignedAlloc(w1sz, 32);
            fixed (short* src = w1[bk]) Unsafe.CopyBlockUnaligned((void*)pw1, (void*)src, w1sz);
            _w1[bk] = (nint)pw1;

            uint b1sz = (uint)(L1数 * sizeof(short));
            var pb1 = (short*)NativeMemory.AlignedAlloc(b1sz, 32);
            fixed (short* src = b1[bk]) Unsafe.CopyBlockUnaligned((void*)pb1, (void*)src, b1sz);
            _b1[bk] = (nint)pb1;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int bk = 0; bk < BK; bk++)
        {
            if (_w1[bk] != 0) { NativeMemory.AlignedFree((void*)_w1[bk]); _w1[bk] = 0; }
            if (_b1[bk] != 0) { NativeMemory.AlignedFree((void*)_b1[bk]); _b1[bk] = 0; }
        }
    }

    // ── スクラッチ計算 ────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void 加算器計算(C盤面 盤面, E手番 視点, int 局面区分, short[] acc)
    {
        short* w = (short*)_w1[局面区分];
        short* b = (short*)_b1[局面区分];

        fixed (short* pAcc = acc)
        {
            // バイアスで初期化（int16 bias → int16 acc: 単純コピー）
            Unsafe.CopyBlockUnaligned(pAcc, b, (uint)(L1数 * sizeof(short)));

            var 敵視点 = 視点 == E手番.先手 ? E手番.後手 : E手番.先手;
            int 自玉升 = To升番号(盤面.Find玉(視点));
            int 敵玉升 = To升番号(盤面.Find玉(敵視点));

            for (int 段 = 1; 段 <= 9; 段++)
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 駒 = 盤面.Get駒(列, 段);
                if (!駒.Is有効) continue;
                if (駒.種類 == E駒種.玉将 || 駒.種類 == E駒種.獅王) continue;
                int 駒種番号 = To駒種番号(駒.種類);
                if (駒種番号 < 0) continue;
                int 升番号  = To升番号(列, 段);
                int 敵区分値 = 駒.手番 == 視点 ? 0 : 1;
                VAdd(pAcc, w + Calc駒位置番号(自玉升, 敵区分値, 駒種番号, 升番号));
            }

            VAdd(pAcc, w + Calc敵玉位置番号(自玉升, 敵玉升));
            Add持駒(pAcc, w, 盤面.先手持ち駒, 自玉升, 視点 == E手番.先手 ? 0 : 1);
            Add持駒(pAcc, w, 盤面.後手持ち駒, 自玉升, 視点 == E手番.後手 ? 0 : 1);
        }
    }

    // ── インクリメンタル更新 ─────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void 適用後加算器更新(
        C盤面 盤面, E手番 視点, int 局面区分, short[] 加算器, S手 手, S取消情報 取消)
    {
        short* w = (short*)_w1[局面区分];
        var 指した側  = 盤面.手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        E駒種? 移動前駒種 = 手.Is打ち ? null
            : 手.Is成り ? C駒.Get成り前(盤面.Get駒(手.Get移動先).種類)
            : 盤面.Get駒(手.Get移動先).種類;
        C駒 取駒      = 取消.取り駒;
        C駒 獅子中取駒 = 取消.中間取り駒;
        int 自玉升 = To升番号(盤面.Find玉(視点));
        var 指した側持ち駒 = 指した側 == E手番.先手 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;

        fixed (short* pAcc = 加算器)
        {
            if (手.Is打ち)
            {
                var 駒種    = 手.Get打ち駒;
                int 駒種番号 = To駒種番号(駒種);
                if (駒種番号 >= 0)
                {
                    int 移動先升 = To升番号(手.Get移動先);
                    int 敵区分値 = 指した側 == 視点 ? 0 : 1;
                    VAdd(pAcc, w + Calc駒位置番号(自玉升, 敵区分値, 駒種番号, 移動先升));
                    int 現在枚数 = 指した側持ち駒[(int)駒種];
                    Add持駒差分(pAcc, w, 自玉升, 駒種, 視点, 指した側, -1, 現在枚数);
                }
            }
            else if (移動前駒種.HasValue)
            {
                var 元駒種    = 移動前駒種.Value;
                int 元駒種番号 = To駒種番号(元駒種);
                int 移動元升  = To升番号(手.Get移動元);
                int 移動先升  = To升番号(手.Get移動先);
                int 敵区分値  = 指した側 == 視点 ? 0 : 1;

                if (指した側 != 視点 && (元駒種 == E駒種.玉将 || 元駒種 == E駒種.獅王))
                {
                    VSub(pAcc, w + Calc敵玉位置番号(自玉升, 移動元升));
                    VAdd(pAcc, w + Calc敵玉位置番号(自玉升, 移動先升));
                }
                if (元駒種番号 >= 0)
                    VSub(pAcc, w + Calc駒位置番号(自玉升, 敵区分値, 元駒種番号, 移動元升));

                var 移動後駒種    = 手.Is成り ? C駒.Get成り後(元駒種) : 元駒種;
                int 移動後駒種番号 = To駒種番号(移動後駒種);
                if (移動後駒種番号 >= 0)
                    VAdd(pAcc, w + Calc駒位置番号(自玉升, 敵区分値, 移動後駒種番号, 移動先升));

                if (取駒.Is有効)
                {
                    int 取敵区分 = 取駒.手番 == 視点 ? 0 : 1;
                    int 取駒種番号 = To駒種番号(取駒.種類);
                    if (取駒種番号 >= 0)
                        VSub(pAcc, w + Calc駒位置番号(自玉升, 取敵区分, 取駒種番号, 移動先升));
                    var 取基本種 = C駒.Get成り前(取駒.種類);
                    int 取現在枚数 = 指した側持ち駒[(int)取基本種];
                    Add持駒差分(pAcc, w, 自玉升, 取基本種, 視点, 指した側, +1, 取現在枚数);
                }
                if (獅子中取駒.Is有効)
                {
                    int 中間升    = To升番号(手.Get中間);
                    int 中間敵区分 = 獅子中取駒.手番 == 視点 ? 0 : 1;
                    int 中間駒種番号 = To駒種番号(獅子中取駒.種類);
                    if (中間駒種番号 >= 0)
                        VSub(pAcc, w + Calc駒位置番号(自玉升, 中間敵区分, 中間駒種番号, 中間升));
                    var 中間基本種 = C駒.Get成り前(獅子中取駒.種類);
                    int 中間現在枚数 = 指した側持ち駒[(int)中間基本種];
                    Add持駒差分(pAcc, w, 自玉升, 中間基本種, 視点, 指した側, +1, 中間現在枚数);
                }
            }
        }
    }

    // ── uint8 変換 ────────────────────────────────────────────────────────────

    /// <summary>
    /// int16加算器 → ReLU + clip → uint8[L1数]。加算器自体は変更しない。
    /// dst_offset: 先手=0, 後手=L1数 の2視点連結バッファ用。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void ToUint8(short[] acc, byte* dst)
    {
        fixed (short* pAcc = acc)
        {
            if (Avx2.IsSupported)
            {
                var zero = Vector256<short>.Zero;
                var max  = Vector256.Create((short)127);
                for (int j = 0; j < L1数; j += 32)
                {
                    var lo = Avx.LoadVector256(pAcc + j);
                    var hi = Avx.LoadVector256(pAcc + j + 16);
                    lo = Avx2.Max(Avx2.Min(lo, max), zero);
                    hi = Avx2.Max(Avx2.Min(hi, max), zero);
                    var packed32 = Avx2.PackUnsignedSaturate(lo, hi);
                    var permuted = Avx2.Permute4x64(packed32.AsInt64(), 0b_11_01_10_00).AsByte();
                    Unsafe.WriteUnaligned(dst + j, permuted);
                }
            }
            else if (Sse41.IsSupported)
            {
                var zero = Vector128<short>.Zero;
                var max  = Vector128.Create((short)127);
                for (int j = 0; j < L1数; j += 16)
                {
                    var lo = Sse2.LoadVector128(pAcc + j);
                    var hi = Sse2.LoadVector128(pAcc + j + 8);
                    lo = Sse41.Max(Sse41.Min(lo, max), zero);
                    hi = Sse41.Max(Sse41.Min(hi, max), zero);
                    var packed = Sse2.PackUnsignedSaturate(lo, hi);
                    Unsafe.WriteUnaligned(dst + j, packed);
                }
            }
            else
            {
                for (int j = 0; j < L1数; j++)
                    dst[j] = pAcc[j] <= 0 ? (byte)0 : pAcc[j] >= 127 ? (byte)127 : (byte)pAcc[j];
            }
        }
    }

    // ── AVX2 vpaddw / vpsubw ─────────────────────────────────────────────────

    // w_row は 32バイトアライン済み(NativeMemory)なので LoadAlignedVector256 可。
    // pAcc はマネージド short[] なので LoadVector256(非アライン)。

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static void VAdd(short* acc, short* w_row)
    {
        if (Avx2.IsSupported)
        {
            for (int j = 0; j < L1数; j += 16)
                Avx.Store(acc + j, Avx2.Add(
                    Avx.LoadVector256(acc + j),
                    Avx.LoadAlignedVector256(w_row + j)));
        }
        else
        {
            for (int j = 0; j < L1数; j++) acc[j] += w_row[j];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    private static void VSub(short* acc, short* w_row)
    {
        if (Avx2.IsSupported)
        {
            for (int j = 0; j < L1数; j += 16)
                Avx.Store(acc + j, Avx2.Subtract(
                    Avx.LoadVector256(acc + j),
                    Avx.LoadAlignedVector256(w_row + j)));
        }
        else
        {
            for (int j = 0; j < L1数; j++) acc[j] -= w_row[j];
        }
    }

    // ── 持駒ヘルパー ─────────────────────────────────────────────────────────

    private static void Add持駒(short* acc, short* w,
        int[] 持ち駒, int 自玉升, int 敵区分値)
    {
        int 歩枚数 = 持ち駒[(int)E駒種.歩兵];
        VAdd(acc, w + Calc持駒歩番号(自玉升, 敵区分値, 歩枚数区分(歩枚数)));

        for (int si = 0; si < 小駒一覧.Length; si++)
        {
            int cnt = 持ち駒[(int)小駒一覧[si]];
            if (cnt > 0) VAdd(acc, w + Calc持駒小駒番号(自玉升, 敵区分値, si, cnt));
        }
        for (int li = 0; li < 大駒一覧.Length; li++)
        {
            int cnt = 持ち駒[(int)大駒一覧[li]];
            if (cnt > 0) VAdd(acc, w + Calc持駒大駒番号(自玉升, 敵区分値, li, cnt));
        }
    }

    private static void Add持駒差分(short* acc, short* w, int 自玉升,
        E駒種 駒, E手番 視点, E手番 指した側, int delta, int 現在枚数)
    {
        int 敵区分 = 指した側 == 視点 ? 0 : 1;
        int 旧枚数 = 現在枚数 - delta;

        if (駒 == E駒種.歩兵)
        {
            int 旧区分 = 歩枚数区分(旧枚数), 新区分 = 歩枚数区分(現在枚数);
            if (旧区分 == 新区分) return;
            VSub(acc, w + Calc持駒歩番号(自玉升, 敵区分, 旧区分));
            VAdd(acc, w + Calc持駒歩番号(自玉升, 敵区分, 新区分));
            return;
        }
        for (int si = 0; si < 小駒一覧.Length; si++)
        {
            if (小駒一覧[si] != 駒) continue;
            if (旧枚数 > 0)   VSub(acc, w + Calc持駒小駒番号(自玉升, 敵区分, si, 旧枚数));
            if (現在枚数 > 0) VAdd(acc, w + Calc持駒小駒番号(自玉升, 敵区分, si, 現在枚数));
            return;
        }
        for (int li = 0; li < 大駒一覧.Length; li++)
        {
            if (大駒一覧[li] != 駒) continue;
            if (旧枚数 > 0)   VSub(acc, w + Calc持駒大駒番号(自玉升, 敵区分, li, 旧枚数));
            if (現在枚数 > 0) VAdd(acc, w + Calc持駒大駒番号(自玉升, 敵区分, li, 現在枚数));
            return;
        }
    }

    // ── 特徴インデックス計算 ─────────────────────────────────────────────────
    // 戻り値 = 「特徴インデックス × L1数」= 荷重行の先頭オフセット (short単位)

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc駒位置番号(int 自玉升, int 敵区分, int 駒種番号, int 升番号)
        => (駒位置_開始 + 自玉升 * 2 * 14 * 81 + 敵区分 * 14 * 81 + 駒種番号 * 81 + 升番号) * L1数;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc敵玉位置番号(int 自玉升, int 敵玉升)
        => (敵玉位置_開始 + 自玉升 * 81 + 敵玉升) * L1数;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc持駒歩番号(int 自玉升, int 敵区分, int 区分)
        => (持駒歩_開始 + 自玉升 * 2 * 持駒歩区分数 + 敵区分 * 持駒歩区分数 + 区分) * L1数;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc持駒小駒番号(int 自玉升, int 敵区分, int 小駒番号, int 枚数)
        => (持駒小駒_開始 + 自玉升 * 2 * 4 * 4 + 敵区分 * 4 * 4 + 小駒番号 * 4 + 枚数 - 1) * L1数;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc持駒大駒番号(int 自玉升, int 敵区分, int 大駒番号, int 枚数)
        => (持駒大駒_開始 + 自玉升 * 2 * 2 * 2 + 敵区分 * 2 * 2 + 大駒番号 * 2 + 枚数 - 1) * L1数;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int To升番号(S升座標 s) => (s.段 - 1) * 9 + (s.列 - 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int To升番号(int 列, int 段) => (段 - 1) * 9 + (列 - 1);

    private static int To駒種番号(E駒種 種類) => 種類 switch
    {
        E駒種.歩兵 => 0, E駒種.香車 => 1, E駒種.桂馬 => 2, E駒種.銀将 => 3,
        E駒種.金将 => 4, E駒種.角行 => 5, E駒種.飛車 => 6,
        E駒種.と金 => 7, E駒種.竪行 => 8, E駒種.騎兵 => 9,
        E駒種.麒麟 => 10, E駒種.鳳凰 => 11, E駒種.龍馬 => 12, E駒種.龍王 => 13,
        _ => -1
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int 歩枚数区分(int n) => n switch
    {
        0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, < 10 => 5, _ => 6
    };
}
