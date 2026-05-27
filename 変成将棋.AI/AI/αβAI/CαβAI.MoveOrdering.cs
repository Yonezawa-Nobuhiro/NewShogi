using 変成将棋.Models;

namespace 変成将棋.AI.αβAI;

partial class CαβAI
{
    // ── 手順スコアリング ─────────────────────────────────────────────

    private int ScoreMove(C盤面 盤面, S手 手, int 深さIdx, S手 tt最善手 = default)
    {
        // 1. TT最善手
        if (手.移動元 == tt最善手.移動元 && 手.移動先 == tt最善手.移動先 &&
            手.中間   == tt最善手.中間   && 手.手フラグ == tt最善手.手フラグ)
            return 1_000_000;

        // 2. 駒取り（MVV-LVA: 価値の高い駒を価値の低い駒で取ることを優先）
        if (!手.Is打ち)
        {
            var 取り = 盤面.Get駒(手.Get移動先);
            if (取り.Is有効)
            {
                var 攻撃 = 盤面.Get駒(手.Get移動元);
                int 攻撃価値 = 攻撃.Is有効 ? _駒価値[(int)攻撃.種類] : 0;
                return 500_000 + (_駒価値[(int)取り.種類] * 100) - 攻撃価値;
            }
        }

        // 3. Killer Move（静かな手のみ）
        int ki = Math.Min(深さIdx, _killer.GetLength(0) - 1);
        if (手.移動元 == _killer[ki, 0].移動元 && 手.移動先 == _killer[ki, 0].移動先) return 400_000;
        if (手.移動元 == _killer[ki, 1].移動元 && 手.移動先 == _killer[ki, 1].移動先) return 300_000;

        // 4. History + 成りボーナス
        int h = 手.Is打ち ? 0 : _history[手.移動元, 手.移動先];
        return (手.Is成り ? 50 : 0) + h;
    }

    private void 静かな手更新(S手 手, int 深さIdx, int 残深さ)
    {
        if (手.Is打ち) return;
        // Killer: 同じ手でなければシフトして先頭に挿入
        int ki = Math.Min(深さIdx, _killer.GetLength(0) - 1);
        if (手.移動元 != _killer[ki, 0].移動元 || 手.移動先 != _killer[ki, 0].移動先)
        {
            _killer[ki, 1] = _killer[ki, 0];
            _killer[ki, 0] = 手;
        }
        // History: 深さの二乗を加算し、オーバーフロー時に全体を半減
        _history[手.移動元, 手.移動先] += 残深さ * 残深さ;
        if (_history[手.移動元, 手.移動先] > 10_000_000)
            for (int a = 0; a < 256; a++)
                for (int b = 0; b < 256; b++)
                    _history[a, b] >>= 1;
    }
}
