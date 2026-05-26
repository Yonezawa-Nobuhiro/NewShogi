using System.Runtime.CompilerServices;
using 変成将棋.Models;

namespace 変成将棋.AI.駒得評価;

/// <summary>
/// 差分更新対応の駒得評価器。
/// NNUEと同一の型シグネチャ（short[] アキュムレータ）で CαβAI に差し込める。
/// acc[0] = 視点側の（盤上駒の現在価値 + 持ち駒の成り前価値）合計
/// </summary>
public sealed class C駒得評価器
{
    public const int AccSize = 1;

    private readonly int[] _駒価値;  // [E駒種インデックス]

    public C駒得評価器(int[] 駒価値) => _駒価値 = (int[])駒価値.Clone();

    // ── スクラッチ計算 ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void 加算器計算(C盤面 盤面, E手番 視点, int 局面区分, short[] acc)
    {
        int score = 0;
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 盤面.Get駒(列, 段);
            if (駒.Is有効 && 駒.手番 == 視点)
                score += _駒価値[(int)駒.種類];
        }
        var 持ち = 視点 == E手番.先手 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;
        for (int t = 1; t < 持ち.Length; t++)
            score += _駒価値[t] * 持ち[t];
        acc[0] = (short)score;
    }

    // ── 差分更新 ─────────────────────────────────────────────────────

    // Apply 済みの盤面で呼ぶ（盤面.手番は直前に指した側の相手番）。
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void 加算器更新(
        C盤面 盤面, E手番 視点, int 旧局面区分, int 新局面区分,
        short[] acc, S手 手, S取消情報 取消)
    {
        var 指した側 = 盤面.手番 == E手番.先手 ? E手番.後手 : E手番.先手;

        if (指した側 == 視点)
        {
            // 打ち: 持ち駒→盤上（同種・同価値）→変化なし
            if (手.Is打ち) return;

            // 成り: 盤上で価値アップ
            if (手.Is成り)
            {
                var 移動後 = 盤面.Get駒(手.Get移動先)!.種類;
                var 移動前 = C駒.Get成り前(移動後);
                acc[0] += (short)(_駒価値[(int)移動後] - _駒価値[(int)移動前]);
            }

            // 取り: 相手の駒を持ち駒として獲得（盤上成り駒→持ち駒で成り外れ）
            if (取消.取り駒.Is有効)
                acc[0] += (short)_駒価値[(int)C駒.Get成り前(取消.取り駒.種類)];
            if (取消.中間取り駒.Is有効)
                acc[0] += (short)_駒価値[(int)C駒.Get成り前(取消.中間取り駒.種類)];
        }
        else
        {
            // 相手が指した: 視点の駒が取られた
            if (取消.取り駒.Is有効 && 取消.取り駒.手番 == 視点)
                acc[0] -= (short)_駒価値[(int)取消.取り駒.種類];
            if (取消.中間取り駒.Is有効 && 取消.中間取り駒.手番 == 視点)
                acc[0] -= (short)_駒価値[(int)取消.中間取り駒.種類];
        }
    }

    // ── 評価 ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int 加算器から評価(short[] 先手acc, short[] 後手acc, E手番 手番)
    {
        int score = 先手acc[0] - 後手acc[0];
        return 手番 == E手番.先手 ? score : -score;
    }
}
