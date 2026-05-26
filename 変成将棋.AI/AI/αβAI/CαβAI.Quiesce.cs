using 変成将棋.Models;

namespace 変成将棋.AI.αβAI;

partial class CαβAI
{
    // ── 静止探索（駒取りのみ継続して局面を安定させる） ────────────────────

    private int Quiesce(C盤面 盤面, int α, int β, int 加算器深度, CancellationToken ct, int quiesce深さ = 0)
    {
        if (ct.IsCancellationRequested) return 0;

        _stat_qnodes++;
        if (quiesce深さ > _stat_qdepth_max) _stat_qdepth_max = quiesce深さ;

        // depth=0: QSearch無効（即Eval）
        if (quiesce深さ >= 0) return Eval(加算器深度, 盤面);

        // stand-pat: NNUE差分評価（NNUE未使用時はC評価関数にフォールバック）
        int stand_pat = Eval(加算器深度, 盤面);
        if (stand_pat >= β) return β;
        if (stand_pat > α) α = stand_pat;

        // 駒取り手専用生成器を使用（全手生成より高速）
        Span<S手> 候補手集 = stackalloc S手[C合法手生成器.最大手数];
        Span<int> capScores = stackalloc int[C合法手生成器.最大手数];
        int 合法手数 = C合法手生成器.Generate駒取り手(盤面, 候補手集);

        // 各駒取り手のスコア（取る駒の価値）を計算
        for (int i = 0; i < 合法手数; i++)
        {
            // 獅王2回移動は中間取りの場合があるので取消情報から取得できないが、
            // 移動先に駒があればそれ、なければ中間駒（獅王中取り）を使う
            var 移動先駒 = 盤面.Get駒(候補手集[i].Get移動先);
            if (移動先駒.Is有効)
                capScores[i] = _駒価値[(int)移動先駒.種類];
            else if (!候補手集[i].Is打ち)
            {
                var 中間駒 = 盤面.Get駒(候補手集[i].Get中間);
                capScores[i] = 中間駒.Is有効 ? _駒価値[(int)中間駒.種類] : 0;
            }
            else
                capScores[i] = 0;
        }

        // Futility at entry: 最大の駒取りを行ってもマージン内で α に届かないなら全ループをスキップ
        // MARGIN=100（歩1枚分）: 成りの可能性など僅かな誤差を許容してより多くのノードを切る
        const int QFutilityMargin = 100;
        if (合法手数 > 0)
        {
            int maxCap = 0;
            for (int i = 0; i < 合法手数; i++)
                if (capScores[i] > maxCap) maxCap = capScores[i];
            if (stand_pat + maxCap + QFutilityMargin < α) { _stat_q_futility++; return stand_pat; }
        }

        for (int i = 0; i < 合法手数; i++)
        {
            // 遅延選択ソート（大きい駒取りを優先）
            int best = i;
            for (int j = i + 1; j < 合法手数; j++)
                if (capScores[j] > capScores[best]) best = j;
            if (best != i)
            {
                (候補手集[i], 候補手集[best]) = (候補手集[best], 候補手集[i]);
                (capScores[i], capScores[best]) = (capScores[best], capScores[i]);
            }

            // Delta Pruning: ソート済みなのでこの手以降全部 α に届かないなら打ち切り
            if (stand_pat + capScores[i] < α) break;

            // SEE: 移動先で取り返されて最終的に損になる手をスキップ
            // 打ち手は取り返し不要なので対象外。SEE < 0 = 損なら展開しない。
            if (capScores[i] > 0 && !候補手集[i].Is打ち)
            {
                _stat_qsee_checked++;
                if (SEE(盤面, 候補手集[i], capScores[i]) < 0) { _stat_qsee_skipped++; continue; }
            }

            var 取消 = 盤面.Apply(候補手集[i]);
            if (!C合法手生成器.Is自玉安全(盤面, 盤面.手番))
            {
                盤面.Undo(候補手集[i], 取消);
                continue;
            }
            // 相手玉を取った手は即勝ち
            if (!盤面.Find玉(盤面.手番).Is盤内)
            {
                盤面.Undo(候補手集[i], 取消);
                return 詰点数;
            }
            SetDirty(加算器深度 + 1, 候補手集[i], 取消);
            int score = -Quiesce(盤面, -β, -α, 加算器深度 + 1, ct, quiesce深さ + 1);
            盤面.Undo(候補手集[i], 取消);

            if (score >= β) return β;
            if (score > α) α = score;
        }

        return α;
    }

    // ── SEE（Static Exchange Evaluation）─────────────────────────────
    //
    // 目的: ある取り手が「取って取り返されても得か損か」を静的に計算し、
    //       損になる取り合い（SEE < 0）を Quiesce から除外してノード数を削減する。
    //
    // アルゴリズム（1ステップ近似）:
    //   1. Apply して相手が取り返せる最小価値の駒（minResp）を探す
    //   2. 取り返しなし       → capValue（全利得確保）
    //   3. 相手が損（minResp > attVal）→ capValue（相手は取り返さない）
    //   4. 相手が取り返す     → capValue - attVal（こちらの最終利得）
    //   → 完全再帰SEEより計算量が少なく、主な損な取り合いを捕捉できる近似
    //
    // 近似・制限:
    //   - 打ち手は呼び出し側で除外（取り返し不要）
    //   - X線攻撃（スライド駒が隠れた攻撃駒を隠している場合）は未考慮
    //   - Is自玉安全 チェックは省略（SEE内の Apply は合法性チェックなし）
    //   - 2段以上の取り合いは「相手が取り返した後さらに取り返す」ケースを無視

    private int SEE(C盤面 盤面, S手 手, int capValue)
    {
        var att = 盤面.Get駒(手.Get移動元);
        if (!att.Is有効) return capValue;
        int attVal = _駒価値[(int)att.種類];

        // Apply して相手の最小価値の取り返し駒を1手先だけ探す
        var undo = 盤面.Apply(手);

        Span<S手> buf = stackalloc S手[C合法手生成器.最大手数];
        int n = C合法手生成器.Generate駒取り手(盤面, buf);
        int minResp = int.MaxValue;
        S升座標 to = 手.Get移動先;

        for (int i = 0; i < n; i++)
        {
            if (buf[i].Is打ち) continue;
            if (!buf[i].Get移動先.Equals(to)) continue;
            var piece = 盤面.Get駒(buf[i].Get移動元);
            if (!piece.Is有効) continue;
            int v = _駒価値[(int)piece.種類];
            if (v < minResp) minResp = v;
        }

        盤面.Undo(手, undo);

        if (minResp == int.MaxValue) return capValue;  // 取り返せない
        if (minResp > attVal)        return capValue;  // 相手が損なので取り返さない
        return capValue - attVal;                       // 相手が取り返した場合の利得
    }
}
