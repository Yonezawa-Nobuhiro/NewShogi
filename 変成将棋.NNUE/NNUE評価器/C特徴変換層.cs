using System.Numerics;
using System.Runtime.CompilerServices;
using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>特徴変換層 - 局面特徴 → L1加算器の計算とインクリメンタル更新。</summary>
internal sealed class C特徴変換層
{
    private static readonly int _SIMD幅 = Vector<float>.Count;

    // 持駒歩の区分数: 変成将棋には持将棋ルールが存在しないため歩の積み上げ局面が
    // 生じにくく、18枚 one-hot は希少区分が学習不足になる。
    // 0・1・2・3・4・5-9・10以上 の7区分で戦略的な情報を十分表現する。
    private const int 持駒歩区分数 = 7;

    // 特徴グループのオフセット
    private const int 駒位置_開始   = 0;           // 81×2×14×81 = 183,708
    private const int 敵玉位置_開始 = 183_708;     // 81×81       =   6,561
    private const int 持駒歩_開始   = 190_269;     // 81×2×7      =   1,134
    private const int 持駒小駒_開始 = 191_403;     // 81×2×4×4   =   2,592
    private const int 持駒大駒_開始 = 193_995;     // 81×2×2×2   =     648

    // 持駒カテゴリ
    private static readonly E駒種[] 小駒一覧 = [E駒種.香車, E駒種.桂馬, E駒種.銀将, E駒種.金将];
    private static readonly E駒種[] 大駒一覧 = [E駒種.角行, E駒種.飛車];

    // _特徴層荷重 は「特徴量数 × L1数」の行列を1次元配列に展開して格納する。
    // 行 = 特徴インデックス（0〜特徴量数-1）、列 = L1ニューロン（0〜L1数-1）の行優先配置。
    // アクティブな特徴 N に対応する行の先頭位置は N × L1数 であり、
    // その行を加算器に足すことで Sparse Embedding Lookup を実現する。
    private readonly float[][] _特徴層荷重;  // 局面区分別 [局面区分数][特徴量数 × L1数]
    private readonly float[][] _特徴層切片;  // 局面区分別 [局面区分数][L1数]

    internal C特徴変換層(float[][] 特徴層荷重, float[][] 特徴層切片)
    {
        _特徴層荷重 = 特徴層荷重;
        _特徴層切片 = 特徴層切片;
    }

    // ── 局面区分 ─────────────────────────────────────────────────────────────

    internal static int 局面区分番号取得(E駒種 自玉, E駒種 敵玉)
    {
        if (自玉 != E駒種.獅王 && 敵玉 != E駒種.獅王) return 0;
        if (自玉 != E駒種.獅王)                        return 1;
        return 2;  // 自玉 == 獅王 && 敵玉 != 獅王
    }

    // ── スクラッチ計算 ────────────────────────────────────────────────────────

    /// <summary>指定視点の L1 加算器をスクラッチ計算する。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void 加算器計算(C盤面 盤面, E手番 視点, int 局面区分, Span<float> L1)
    {
        var 荷重 = _特徴層荷重[局面区分];
        _特徴層切片[局面区分].CopyTo(L1);

        var 敵視点 = 視点 == E手番.先手 ? E手番.後手 : E手番.先手;
        int 自玉升 = To升番号(盤面.Find玉(視点));
        int 敵玉升 = To升番号(盤面.Find玉(敵視点));

        // 盤上の駒（玉将・獅王を除く）
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 盤面.Get駒(列, 段);
            if (!駒.Is有効) continue;
            if (駒.種類 == E駒種.玉将 || 駒.種類 == E駒種.獅王) continue;

            int 駒種番号 = To駒種番号(駒.種類);
            if (駒種番号 < 0) continue;

            int 升番号   = To升番号(列, 段);
            int 敵区分値 = 駒.手番 == 視点 ? 0 : 1;
            Add荷重行(L1, 荷重, Calc駒位置開始番号(自玉升, 敵区分値, 駒種番号, 升番号), 1f);
        }

        // 敵玉位置
        Add荷重行(L1, 荷重, Calc敵玉位置開始番号(自玉升, 敵玉升), 1f);

        // 持駒
        Add持駒特徴量(L1, 荷重, 盤面.先手持ち駒, 自玉升, 視点 == E手番.先手 ? 0 : 1);
        Add持駒特徴量(L1, 荷重, 盤面.後手持ち駒, 自玉升, 視点 == E手番.後手 ? 0 : 1);
    }

    // ── インクリメンタル更新 ──────────────────────────────────────────────────

    /// <summary>
    /// Apply 後の差分で L1 加算器を更新する。視点の加算器を更新すること。
    ///
    /// 【前提条件】自玉が動いていないこと。
    /// 自玉が動くと全特徴インデックスが変わるため差分更新不可。その場合は加算器計算（スクラッチ）を使うこと。
    ///
    /// 【現在未接続】CαβAI.cs 側に探索木ノードごとの加算器スタック管理を実装した後に使用する。
    /// それまでは Evaluate（スクラッチ）が代わりに呼ばれる。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal void 適用後加算器更新(
        C盤面 盤面, E手番 視点, int 局面区分, float[] 加算器,
        S手 手, S取消情報 取消)
    {
        // Apply後は盤面.手番が反転しているため、指した側はその逆
        var 指した側 = 盤面.手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        // 移動前駒種: 移動先にいる駒が移動後。成りなら成る前の種類に戻す。
        E駒種? 移動前駒種 = 手.Is打ち ? null
            : 手.Is成り ? C駒.Get成り前(盤面.Get駒(手.Get移動先).種類)
            : 盤面.Get駒(手.Get移動先).種類;
        C駒 取駒        = 取消.取り駒;
        C駒 獅子中取駒   = 取消.中間取り駒;
        var 荷重   = _特徴層荷重[局面区分];
        int 自玉升 = To升番号(盤面.Find玉(視点));
        var 指した側持ち駒 = 指した側 == E手番.先手 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;

        if (手.Is打ち)
        {
            var 駒種    = 手.Get打ち駒;
            int 駒種番号 = To駒種番号(駒種);
            if (駒種番号 >= 0)
            {
                int 移動先升 = To升番号(手.Get移動先);
                int 敵区分値 = 指した側 == 視点 ? 0 : 1;
                Add荷重行(加算器, 荷重, Calc駒位置開始番号(自玉升, 敵区分値, 駒種番号, 移動先升), 1f);
                int 現在枚数 = 指した側持ち駒[(int)駒種];
                Add持駒差分(加算器, 荷重, 自玉升, 駒種, 視点, 指した側, -1, 現在枚数);
            }
        }
        else if (移動前駒種.HasValue)
        {
            var 元駒種     = 移動前駒種.Value;
            int 元駒種番号  = To駒種番号(元駒種);
            int 移動元升   = To升番号(手.Get移動元);
            int 移動先升   = To升番号(手.Get移動先);
            int 敵区分値   = 指した側 == 視点 ? 0 : 1;

            // 敵玉移動 → 敵玉位置特徴を差分更新
            if (指した側 != 視点 && (元駒種 == E駒種.玉将 || 元駒種 == E駒種.獅王))
            {
                Add荷重行(加算器, 荷重, Calc敵玉位置開始番号(自玉升, 移動元升), -1f);
                Add荷重行(加算器, 荷重, Calc敵玉位置開始番号(自玉升, 移動先升),  1f);
            }

            // 移動元を除去
            if (元駒種番号 >= 0)
                Add荷重行(加算器, 荷重, Calc駒位置開始番号(自玉升, 敵区分値, 元駒種番号, 移動元升), -1f);

            // 移動先に追加（成りあり）
            var 移動後駒種    = 手.Is成り ? C駒.Get成り後(元駒種) : 元駒種;
            int 移動後駒種番号 = To駒種番号(移動後駒種);
            if (移動後駒種番号 >= 0)
                Add荷重行(加算器, 荷重, Calc駒位置開始番号(自玉升, 敵区分値, 移動後駒種番号, 移動先升), 1f);

            // 取り駒の除去 + 持駒増加
            if (取駒.Is有効)
            {
                int 取駒敵区分値 = 取駒.手番 == 視点 ? 0 : 1;
                int 取駒種番号  = To駒種番号(取駒.種類);
                if (取駒種番号 >= 0)
                    Add荷重行(加算器, 荷重, Calc駒位置開始番号(自玉升, 取駒敵区分値, 取駒種番号, 移動先升), -1f);
                var 取駒基本種 = C駒.Get成り前(取駒.種類);
                int 取駒現在枚数 = 指した側持ち駒[(int)取駒基本種];
                Add持駒差分(加算器, 荷重, 自玉升, 取駒基本種, 視点, 指した側, +1, 取駒現在枚数);
            }

            // 獅王中間取り
            if (獅子中取駒.Is有効)
            {
                int 中間升番号    = To升番号(手.Get中間);
                int 中間駒敵区分値 = 獅子中取駒.手番 == 視点 ? 0 : 1;
                int 中間駒種番号  = To駒種番号(獅子中取駒.種類);
                if (中間駒種番号 >= 0)
                    Add荷重行(加算器, 荷重, Calc駒位置開始番号(自玉升, 中間駒敵区分値, 中間駒種番号, 中間升番号), -1f);
                var 中間駒基本種 = C駒.Get成り前(獅子中取駒.種類);
                int 中間駒現在枚数 = 指した側持ち駒[(int)中間駒基本種];
                Add持駒差分(加算器, 荷重, 自玉升, 中間駒基本種, 視点, 指した側, +1, 中間駒現在枚数);
            }
        }
    }

    // ── ヘルパー ─────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int To升番号(S升座標 升座標) => (升座標.段 - 1) * 9 + (升座標.列 - 1);

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

    // 歩枚数 → 区分インデックス (0-6)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int 歩枚数区分(int 枚数) => 枚数 switch
    {
        0 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4,
        < 10 => 5,
        _ => 6
    };

    // ── 特徴インデックス → 荷重テーブル開始番号 ──────────────────────────────
    // 各特徴は多次元配列を行優先で1次元化したもの。
    // 戻り値は「特徴インデックス × L1数」= Add荷重行 の開始番号に直接渡せる値。

    // [自玉升(81)][敵味方(2)][駒種(14)][駒位置(81)] の4次元テーブル
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc駒位置開始番号(int 自玉升, int 敵区分値, int 駒種番号, int 升番号)
        => (駒位置_開始 + 自玉升 * 2 * 14 * 81 + 敵区分値 * 14 * 81 + 駒種番号 * 81 + 升番号)
           * CNNUE評価器.L1数;

    // [自玉升(81)][敵玉位置(81)] の2次元テーブル
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc敵玉位置開始番号(int 自玉升, int 敵玉升)
        => (敵玉位置_開始 + 自玉升 * 81 + 敵玉升) * CNNUE評価器.L1数;

    // [自玉升(81)][敵味方(2)][歩区分(7)] の3次元テーブル
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc持駒歩開始番号(int 自玉升, int 敵区分値, int 歩区分)
        => (持駒歩_開始 + 自玉升 * 2 * 持駒歩区分数 + 敵区分値 * 持駒歩区分数 + 歩区分)
           * CNNUE評価器.L1数;

    // [自玉升(81)][敵味方(2)][小駒種(4)][枚数(4)] の4次元テーブル（枚数は1始まり）
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc持駒小駒開始番号(int 自玉升, int 敵区分値, int 小駒番号, int 枚数)
        => (持駒小駒_開始 + 自玉升 * 2 * 4 * 4 + 敵区分値 * 4 * 4 + 小駒番号 * 4 + 枚数 - 1)
           * CNNUE評価器.L1数;

    // [自玉升(81)][敵味方(2)][大駒種(2)][枚数(2)] の4次元テーブル（枚数は1始まり）
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Calc持駒大駒開始番号(int 自玉升, int 敵区分値, int 大駒番号, int 枚数)
        => (持駒大駒_開始 + 自玉升 * 2 * 2 * 2 + 敵区分値 * 2 * 2 + 大駒番号 * 2 + 枚数 - 1)
           * CNNUE評価器.L1数;

    private static void Add持駒特徴量(Span<float> L1, float[] 荷重,
        int[] 持ち駒, int 自玉升, int 敵区分値)
    {
        // 歩: 0枚を含む7区分 one-hot（常にいずれか1つがアクティブ）
        int 歩枚数 = 持ち駒[(int)E駒種.歩兵];
        Add荷重行(L1, 荷重, Calc持駒歩開始番号(自玉升, 敵区分値, 歩枚数区分(歩枚数)), 1f);

        for (int si = 0; si < 小駒一覧.Length; si++)
        {
            int cnt = 持ち駒[(int)小駒一覧[si]];
            if (cnt <= 0) continue;
            Add荷重行(L1, 荷重, Calc持駒小駒開始番号(自玉升, 敵区分値, si, cnt), 1f);
        }

        for (int li = 0; li < 大駒一覧.Length; li++)
        {
            int cnt = 持ち駒[(int)大駒一覧[li]];
            if (cnt <= 0) continue;
            Add荷重行(L1, 荷重, Calc持駒大駒開始番号(自玉升, 敵区分値, li, cnt), 1f);
        }
    }

    // 現在枚数: Apply 後の枚数（呼び出し元が盤面から取得して渡す）
    private static void Add持駒差分(float[] 加算器, float[] 荷重, int 自玉升,
        E駒種 対象駒, E手番 視点, E手番 指した側, int delta, int 現在枚数)
    {
        int 敵区分 = 指した側 == 視点 ? 0 : 1;
        int 旧枚数 = 現在枚数 - delta;

        if (対象駒 == E駒種.歩兵)
        {
            int 旧区分 = 歩枚数区分(旧枚数);
            int 新区分 = 歩枚数区分(現在枚数);
            if (旧区分 == 新区分) return;
            Add荷重行(加算器, 荷重, Calc持駒歩開始番号(自玉升, 敵区分, 旧区分), -1f);
            Add荷重行(加算器, 荷重, Calc持駒歩開始番号(自玉升, 敵区分, 新区分),  1f);
            return;
        }

        for (int si = 0; si < 小駒一覧.Length; si++)
        {
            if (小駒一覧[si] != 対象駒) continue;
            if (旧枚数 > 0)   Add荷重行(加算器, 荷重, Calc持駒小駒開始番号(自玉升, 敵区分, si, 旧枚数),   -1f);
            if (現在枚数 > 0) Add荷重行(加算器, 荷重, Calc持駒小駒開始番号(自玉升, 敵区分, si, 現在枚数),  1f);
            return;
        }

        for (int li = 0; li < 大駒一覧.Length; li++)
        {
            if (大駒一覧[li] != 対象駒) continue;
            if (旧枚数 > 0)   Add荷重行(加算器, 荷重, Calc持駒大駒開始番号(自玉升, 敵区分, li, 旧枚数),   -1f);
            if (現在枚数 > 0) Add荷重行(加算器, 荷重, Calc持駒大駒開始番号(自玉升, 敵区分, li, 現在枚数),  1f);
            return;
        }
    }

    // 荷重テーブルの1行（L1数次元）を加算器に加算する SIMD ヘルパー。
    //
    // 一次変換 v_i = Σ w_ij x_j + b_i に対して x_j が Δx 変化したときの
    // v_i への寄与 Δv_i = w_ij Δx を更新する。
    //
    // 引数:
    //   開始番号 … 荷重テーブル内の開始番号（= 特徴インデックス × L1数）
    //   変分     … +1f=加算 / -1f=除去 / その他=スカラー乗算後加算
    // scale = ±1 の場合は乗算を省略して高速化。
    // 2つのオーバーロードは加算器の型のみ異なる:
    //   Span<float> → スクラッチ計算（加算器計算）用
    //   float[]     → インクリメンタル更新（適用後加算器更新）用
    //
    // SIMD 化前の素朴な実装（参考）:
    //   for (int j = 0; j < L1数; j++) 加算器[j] += 荷重[開始番号 + j] * 変分;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add荷重行(Span<float> 加算器, float[] 荷重, int 開始番号, float 変分)
    {
        int SIMD幅 = _SIMD幅;  // ローカルに落とすことで JIT がループ中レジスタ保持しやすくなる
        if (変分 == 1f)
        {
            int j = 0;
            for (; j <= CNNUE評価器.L1数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(加算器[j..]) + new Vector<float>(荷重, 開始番号 + j)).CopyTo(加算器[j..]);
            for (; j < CNNUE評価器.L1数; j++)
                加算器[j] += 荷重[開始番号 + j];
        }
        else if (変分 == -1f)
        {
            int j = 0;
            for (; j <= CNNUE評価器.L1数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(加算器[j..]) - new Vector<float>(荷重, 開始番号 + j)).CopyTo(加算器[j..]);
            for (; j < CNNUE評価器.L1数; j++)
                加算器[j] -= 荷重[開始番号 + j];
        }
        else
        {
            var SIMDスケール = new Vector<float>(変分);
            int j = 0;
            for (; j <= CNNUE評価器.L1数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(加算器[j..]) + new Vector<float>(荷重, 開始番号 + j) * SIMDスケール).CopyTo(加算器[j..]);
            for (; j < CNNUE評価器.L1数; j++)
                加算器[j] += 荷重[開始番号 + j] * 変分;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add荷重行(float[] 加算器, float[] 荷重, int 開始番号, float 変分)
    {
        int SIMD幅 = _SIMD幅;  // ローカルに落とすことで JIT がループ中レジスタ保持しやすくなる
        if (変分 == 1f)
        {
            int j = 0;
            for (; j <= CNNUE評価器.L1数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(加算器, j) + new Vector<float>(荷重, 開始番号 + j)).CopyTo(加算器, j);
            for (; j < CNNUE評価器.L1数; j++)
                加算器[j] += 荷重[開始番号 + j];
        }
        else if (変分 == -1f)
        {
            int j = 0;
            for (; j <= CNNUE評価器.L1数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(加算器, j) - new Vector<float>(荷重, 開始番号 + j)).CopyTo(加算器, j);
            for (; j < CNNUE評価器.L1数; j++)
                加算器[j] -= 荷重[開始番号 + j];
        }
        else
        {
            var SIMDスケール = new Vector<float>(変分);
            int j = 0;
            for (; j <= CNNUE評価器.L1数 - SIMD幅; j += SIMD幅)
                (new Vector<float>(加算器, j) + new Vector<float>(荷重, 開始番号 + j) * SIMDスケール).CopyTo(加算器, j);
            for (; j < CNNUE評価器.L1数; j++)
                加算器[j] += 荷重[開始番号 + j] * 変分;
        }
    }
}
