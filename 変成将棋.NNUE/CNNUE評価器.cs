using System.Numerics;
using System.Runtime.CompilerServices;
using 変成将棋.Models;

namespace 変成将棋.NNUE;

/// <summary>
/// NNUE 評価器（変成将棋版 HalfKP + 王駒種局面区分）。
///
/// 特徴量 (194,643次元 / 視点):
///   駒位置  : 自玉位置(81) × 敵味方(2) × 駒種14 × 駒位置(81) = 183,708
///   敵玉位置: 自玉位置(81) × 敵玉位置(81)                     =   6,561
///   持駒歩  : 自玉位置(81) × 敵味方(2) × 区分(7)              =   1,134
///   持駒小駒: 自玉位置(81) × 敵味方(2) × 4種 × 枚数(4)        =   2,592
///   持駒大駒: 自玉位置(81) × 敵味方(2) × 2種 × 枚数(2)        =     648
///                                                  合計        = 194,643
///
/// 王駒種局面区分 (3種):
///   0: 自玉=玉将, 敵玉=玉将
///   1: 自玉=玉将, 敵玉=獅王
///   2: 自玉=獅王, 敵玉=玉将  ※両方獅王はルール上不可
///
/// ネットワーク:
///   L1 × 2視点 → 512 (ReLU) → 64 (ReLU) → 1
///
/// 荷重ファイル形式 (nnue_weights_halfkp.bin):
///   Magic "NHKP" (4 bytes)
///   W1[3][特徴量数 × L1数]  float32
///   B1[3][L1数]             float32
///   W2[(L1数*2) × L2数]     float32
///   B2[L2数]                float32
///   W3[L2数]                float32
///   B3                      float32
///
/// クラス構成:
///   CNNUE評価器  ← 統合・Load・Evaluate・加算器から評価
///   C特徴変換層  ← 特徴→L1加算器（スクラッチ／インクリメンタル）
///   C中間層      ← 結合L1→L2（ReLU込み）
///   C出力層      ← L2→スカラー
/// </summary>
public sealed class CNNUE評価器
{
    // ── 定数 ─────────────────────────────────────────────────────────────────
    public const int 特徴量数  = 194_643;
    public const int L1数       = 256;  // 特徴変換層の出力サイズ（1視点あたり）。2視点連結で L1数×2 が中間層への入力。
    public const int L2数       = 64;   // 中間層の出力サイズ。
    public const int 局面区分数  = 3;

    private static readonly int _SIMD幅 = Vector<float>.Count;

    // ── フィールド ────────────────────────────────────────────────────────────
    private readonly C特徴変換層 _特徴変換層;
    private readonly C中間層     _中間層;
    private readonly C出力層     _出力層;

    private CNNUE評価器(C特徴変換層 特徴変換層, C中間層 中間層, C出力層 出力層)
    {
        _特徴変換層 = 特徴変換層;
        _中間層     = 中間層;
        _出力層     = 出力層;
    }

    // ── ロード ───────────────────────────────────────────────────────────────

    public static CNNUE評価器? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            // マジックバイト "NHKP" = NNUE HalfKP のファイル識別子
            // 不一致の場合は誤ったファイルと判断して null を返す
            var magic = br.ReadBytes(4);
            if (magic[0] != 'N' || magic[1] != 'H' || magic[2] != 'K' || magic[3] != 'P')
                return null;

            float[] ReadFloats(int n)
            {
                var arr   = new float[n];
                var bytes = br.ReadBytes(n * 4);
                Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
                return arr;
            }

            var 特徴層荷重 = new float[局面区分数][];
            var 特徴層切片 = new float[局面区分数][];
            for (int b = 0; b < 局面区分数; b++)
            {
                特徴層荷重[b] = ReadFloats(特徴量数 * L1数);
                特徴層切片[b] = ReadFloats(L1数);
            }
            var 中間層荷重 = ReadFloats(L1数 * 2 * L2数);
            var 中間層切片 = ReadFloats(L2数);
            var 出力層荷重 = ReadFloats(L2数);
            var 出力層切片 = br.ReadSingle();

            return new CNNUE評価器(
                new C特徴変換層(特徴層荷重, 特徴層切片),
                new C中間層(中間層荷重, 中間層切片),
                new C出力層(出力層荷重, 出力層切片));
        }
        catch { return null; }
    }

    // ── 局面区分選択 ──────────────────────────────────────────────────────────

    /// <summary>自玉・敵玉の駒種から局面区分番号を返す。</summary>
    public static int 局面区分番号取得(E駒種 自玉, E駒種 敵玉)
        => C特徴変換層.局面区分番号取得(自玉, 敵玉);

    // ── 加算器更新（スクラッチ／インクリメンタルの自動切り替え）────────────────

    /// <summary>
    /// 手を適用した後に加算器を更新する。
    /// 自玉が動いた場合は全再計算、それ以外は差分更新を内部で自動的に選択する。
    /// αβ 探索側はこのメソッドだけを呼べばよく、切り替え判断を知る必要はない。
    /// </summary>
    public void 加算器更新(
        C盤面 盤面, E手番 視点, int 旧局面区分, int 新局面区分, float[] 加算器,
        S手 手, S取消情報 取消)
    {
        if (旧局面区分 != 新局面区分 || 全再計算が必要(盤面, 手, 視点))
            _特徴変換層.加算器計算(盤面, 視点, 新局面区分, 加算器);
        else
            _特徴変換層.適用後加算器更新(盤面, 視点, 新局面区分, 加算器, 手, 取消);
    }

    // 自玉が動いた場合、全特徴インデックスが変わるため差分更新が不可能
    // Apply後は盤面.手番が反転しているため、指した側はその逆で求める
    private static bool 全再計算が必要(C盤面 盤面, S手 手, E手番 視点)
    {
        if (手.Is打ち) return false;
        var 指した側 = 盤面.手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        var 移動後駒種 = 盤面.Get駒(手.Get移動先)!.種類;
        var 移動前駒種 = 手.Is成り ? C駒.Get成り前(移動後駒種) : 移動後駒種;
        return 指した側 == 視点
               && (移動前駒種 == E駒種.玉将 || 移動前駒種 == E駒種.獅王);
    }

    // ── 評価 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// ルートノードの評価（スクラッチ計算）。
    /// αβ 探索開始時と加算器スタックが無効な場合に使う。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Evaluate(C盤面 盤面)
    {
        var 先手玉駒種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
        var 後手玉駒種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;

        Span<float> 先手L1 = stackalloc float[L1数];
        Span<float> 後手L1 = stackalloc float[L1数];

        _特徴変換層.加算器計算(盤面, E手番.先手, 局面区分番号取得(先手玉駒種, 後手玉駒種), 先手L1);
        _特徴変換層.加算器計算(盤面, E手番.後手, 局面区分番号取得(後手玉駒種, 先手玉駒種), 後手L1);

        return 加算器から評価(先手L1, 後手L1, 盤面.手番);
    }

    /// <summary>
    /// αβ 探索中の評価。Apply 後の盤面・手・取消情報を受け取る。
    /// 加算器スタック実装後にインクリメンタル更新へ切り替える。
    /// 現在はスクラッチ計算にフォールバック。
    /// </summary>
    public int Evaluate(C盤面 盤面, S手 手, S取消情報 取消)
        => Evaluate(盤面);

    // ── αβ 向け公開 API ─────────────────────────────────────────────────────

    /// <summary>
    /// 指定視点の L1 加算器をスクラッチ計算する。
    /// αβ のルートノードで加算器を初期化するために使う。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void 加算器計算(C盤面 盤面, E手番 視点, int 局面区分, Span<float> L1)
        => _特徴変換層.加算器計算(盤面, 視点, 局面区分, L1);

    // ── 加算器から評価 ────────────────────────────────────────────────────────

    /// <summary>2つの L1 加算器から評価値を計算する（手番側視点の centipawn）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int 加算器から評価(ReadOnlySpan<float> 先手L1Raw, ReadOnlySpan<float> 後手L1Raw, E手番 手番)
    {
        int SIMD幅 = _SIMD幅;

        // スタックの加算器を変更しないよう一時バッファへコピーしてから ReLU
        Span<float> 先手L1 = stackalloc float[L1数];
        Span<float> 後手L1 = stackalloc float[L1数];
        先手L1Raw.CopyTo(先手L1);
        後手L1Raw.CopyTo(後手L1);

        var ゼロ = Vector<float>.Zero;
        for (int i = 0; i <= L1数 - SIMD幅; i += SIMD幅)
        {
            Vector.Max(new Vector<float>(先手L1[i..]), ゼロ).CopyTo(先手L1[i..]);
            Vector.Max(new Vector<float>(後手L1[i..]), ゼロ).CopyTo(後手L1[i..]);
        }
        for (int i = (L1数 / SIMD幅) * SIMD幅; i < L1数; i++)
        {
            if (先手L1[i] < 0) 先手L1[i] = 0;
            if (後手L1[i] < 0) 後手L1[i] = 0;
        }

        // 手番側が先頭 → [手番 | 手番でない]
        Span<float> 結合L1 = stackalloc float[L1数 * 2];
        if (手番 == E手番.先手)
        {
            先手L1.CopyTo(結合L1);
            後手L1.CopyTo(結合L1[L1数..]);
        }
        else
        {
            後手L1.CopyTo(結合L1);
            先手L1.CopyTo(結合L1[L1数..]);
        }

        // 中間層 → 出力層
        Span<float> L2出力 = stackalloc float[L2数];
        _中間層.Forward(結合L1, L2出力);
        return (int)(_出力層.Forward(L2出力) * 2000f);
    }
}
