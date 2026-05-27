using System.Numerics;
using System.Runtime.CompilerServices;
using 変成将棋.Models;

namespace 変成将棋.AI.駒得評価;

/// <summary>
/// 差分更新対応の駒得評価器。
/// acc = 視点側の（盤上駒の現在価値 + 持ち駒の成り前価値）合計（int で保持）
/// </summary>
public sealed class C駒得評価器
{
    private readonly int[]   _駒価値b;  // [E駒種インデックス 0..16]、インデックス0は未使用
    private readonly short[] _駒価値s;  // [0..15] = E駒種.歩兵(1)..獅王(16)、Vector<short>用

    public C駒得評価器(int[] 駒価値)
    {
        _駒価値b = (int[])駒価値.Clone();
        int vw = Vector<short>.Count;
        int len = (16 + vw - 1) / vw * vw;
        _駒価値s = new short[len];
        for (int i = 0; i < 16; i++) _駒価値s[i] = (short)駒価値[i + 1];
    }

    // ── スクラッチ計算 ────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void 加算器計算(C盤面 盤面, E手番 視点, int 局面区分, ref int acc)
    {
        var 持ち = 視点 == E手番.先手 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;

        Span<short> counts = stackalloc short[_駒価値s.Length];
        for (int i = 0; i < 16; i++)
            counts[i] = (short)(盤面.Get駒ビット(視点, (E駒種)(i + 1)).PopCount() + 持ち[i + 1]);

        int vw = Vector<short>.Count;
        int score = 0;
        int k = 0;
        for (; k <= 16 - vw; k += vw)
        {
            var vProd = new Vector<short>(counts[k..]) * new Vector<short>(_駒価値s, k);
            Vector.Widen(vProd, out var lo, out var hi);
            score += Vector.Sum(lo) + Vector.Sum(hi);
        }
        for (; k < 16; k++)
            score += counts[k] * _駒価値s[k];

        acc = score;
    }

    // ── 差分更新 ─────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void 加算器更新(
        C盤面 盤面, E手番 視点, int 旧局面区分, int 新局面区分,
        ref int acc, S手 手, S取消情報 取消)
    {
        var 指した側 = 盤面.手番 == E手番.先手 ? E手番.後手 : E手番.先手;

        if (指した側 == 視点)
        {
            if (手.Is打ち) return;

            if (手.Is成り)
            {
                var 移動後 = 盤面.Get駒(手.Get移動先)!.種類;
                var 移動前 = C駒.Get成り前(移動後);
                acc += _駒価値b[(int)移動後] - _駒価値b[(int)移動前];
            }

            if (取消.取り駒.Is有効)
                acc += _駒価値b[(int)C駒.Get成り前(取消.取り駒.種類)];
            if (取消.中間取り駒.Is有効)
                acc += _駒価値b[(int)C駒.Get成り前(取消.中間取り駒.種類)];
        }
        else
        {
            if (取消.取り駒.Is有効 && 取消.取り駒.手番 == 視点)
                acc -= _駒価値b[(int)取消.取り駒.種類];
            if (取消.中間取り駒.Is有効 && 取消.中間取り駒.手番 == 視点)
                acc -= _駒価値b[(int)取消.中間取り駒.種類];
        }
    }

    // ── 評価 ──────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int 加算器から評価(ref int 先手acc, ref int 後手acc, E手番 手番)
    {
        int score = 先手acc - 後手acc;
        return 手番 == E手番.先手 ? score : -score;
    }
}
