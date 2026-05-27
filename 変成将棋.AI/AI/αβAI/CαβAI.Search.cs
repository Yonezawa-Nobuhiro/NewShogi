using 変成将棋.Models;

namespace 変成将棋.AI.αβAI;

partial class CαβAI
{
    // ── ルートノード探索 ──────────────────────────────────────────────

    private (S手 最善手, int 点数) SearchRoot(C盤面 盤面, Span<S手> 候補手集, int 深度,
                                              int α, int β, CancellationToken ct)
    {
        S手 最善手 = 候補手集[0];
        int n  = 候補手集.Length;

        // ルートの加算器を初期化
        Init加算器Root(盤面);

        Span<int> 点数集 = stackalloc int[n];
        for (int i = 0; i < n; i++) 点数集[i] = ScoreMove(盤面, 候補手集[i], 0);

        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested) break;

            // 遅延選択ソート
            int best = i;
            for (int j = i + 1; j < n; j++)
                if (点数集[j] > 点数集[best]) best = j;
            if (best != i)
            {
                (候補手集[i], 候補手集[best]) = (候補手集[best], 候補手集[i]);
                (点数集[i],   点数集[best])   = (点数集[best],   点数集[i]);
            }

            var 取消 = 盤面.Apply(候補手集[i]);
            // 相手玉を取った手は即勝ち
            if (!盤面.Find玉(盤面.手番).Is盤内)
            {
                盤面.Undo(候補手集[i], 取消);
                return (候補手集[i], 詰点数);
            }
            SetDirty(1, 候補手集[i], 取消);
            int 点数 = -Search(盤面, 深度 - 1, -β, -α, 1, ct);
            盤面.Undo(候補手集[i], 取消);

            if (点数 > α)
            {
                α     = 点数;
                最善手 = 候補手集[i];
            }
        }
        return (最善手, α);
    }

    // ── Negamax + αβ枝刈り + 置換表 ─────────────────────────────────

    private int Search(C盤面 盤面, int 残深さ, int α, int β, int 加算器深度,
                       CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return 0;

        if (残深さ <= 0)
            return Quiesce(盤面, α, β, 加算器深度, ct);

        _stat_nodes++;

        // 千日手検出: 現在の局面が探索経路に既出なら引き分け(0)
        ulong hash = 盤面.αβハッシュ;
        for (int hi = 0; hi < _履歴数; hi++)
            if (_ハッシュ履歴[hi] == hash) return 0;

        _ハッシュ履歴[_履歴数++] = hash;
        int ret = SearchCore(盤面, 残深さ, α, β, 加算器深度, hash, ct);
        _履歴数--;
        return ret;
    }

    private int SearchCore(C盤面 盤面, int 残深さ, int α, int β, int 加算器深度,
                           ulong hash, CancellationToken ct)
    {
        // 置換表ルックアップ
        bool ttヒット = _置換表.検索(hash, 残深さ, α, β, out int ttスコア, out S手 tt最善手);
        if (ttヒット) { _stat_ttヒット++; return ttスコア; }

        // 高速1手詰み判定（残深さ >= 2 で呼ぶ。残深さ1以下は Quiesce に任せる）
        if (残深さ >= 2)
        {
            var 詰み手 = C詰将棋探索.Get1手詰み(盤面);
            if (詰み手.HasValue) return 詰点数 - 1;
        }

        // Is自玉安全・staticEvalを1回だけ計算してNMP/Futility両方で共用
        // 自玉不安全（王手）ノードは NMP も Futility も不要なので評価計算をスキップ
        // ただし加算器は子ノードのために更新しておく必要がある
        bool 自玉安全 = C合法手生成器.Is自玉安全(盤面, 盤面.手番);
        int staticEval;
        if (自玉安全)
        {
            staticEval = Eval(加算器深度, 盤面);
        }
        else
        {
            if (_加算器dirty![加算器深度])
                Refresh加算器(加算器深度, 盤面);
            staticEval = 0;

            // Check Extension: 王手中は探索深さを+1延長（スタック上限内のみ）
            if (加算器深度 < _加算器dirty.Length - 3)
                残深さ++;
        }

// Null Move Pruning: 王手されていない かつ staticEval >= β のとき手番をパスして浅く探索
        if (残深さ >= 3 && 自玉安全 && staticEval >= β)
        {
            _stat_nmp発動++;
            盤面.ApplyNullMove();
            int nullScore = -Search(盤面, 残深さ - 3, -β, -β + 1, 加算器深度, ct);
            盤面.UndoNullMove();
            if (nullScore >= β) return β;
        }

        // Futility Pruning: 浅いノードで staticEval + マージン < α なら静かな手をスキップ
        // 残深さ1=飛車1枚分、残深さ2=飛車2枚分 をマージンとして使用
        int futilityMargin = 残深さ <= 1 ? _駒価値[(int)E駒種.飛車]
                           : 残深さ == 2 ? _駒価値[(int)E駒種.飛車] * 2 : int.MaxValue;
        bool futilityActive = 自玉安全 && futilityMargin < int.MaxValue
                           && staticEval + futilityMargin < α;

        Span<S手> 候補手集 = stackalloc S手[C合法手生成器.最大手数];
        int 合法手数 = C合法手生成器.Generate擬似合法手(盤面, 候補手集);

        Span<int> scores = stackalloc int[合法手数];
        for (int i = 0; i < 合法手数; i++)
            scores[i] = ScoreMove(盤面, 候補手集[i], 加算器深度, tt最善手);

        int 元α = α;
        S手 最善手 = default;
        bool 合法手あり = false;
        for (int i = 0; i < 合法手数; i++)
        {
            int best = i;
            for (int j = i + 1; j < 合法手数; j++)
                if (scores[j] > scores[best]) best = j;
            if (best != i)
            {
                (候補手集[i], 候補手集[best]) = (候補手集[best], 候補手集[i]);
                (scores[i],   scores[best])   = (scores[best],   scores[i]);
            }

            var 指した側 = 盤面.手番;
            var 取消 = 盤面.Apply(候補手集[i]);
            if (!C合法手生成器.Is自玉安全(盤面, 指した側))
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
            合法手あり = true;

            // Futility Pruning: 静かな手（駒取り・成りでない）をスキップ
            if (futilityActive && scores[i] < 500_000 && !候補手集[i].Is成り)
            {
                盤面.Undo(候補手集[i], 取消);
                continue;
            }

            SetDirty(加算器深度 + 1, 候補手集[i], 取消);

            // LMR: 3手目以降の静かな手（駒取り・TT最善手でない）は depth-2 で探索（深さ5以上で効果的）
            int reduction = (i >= 2 && 残深さ >= 4 && scores[i] < 95) ? 2 : 0;
            if (reduction > 0) _stat_lmr適用++;
            int score = -Search(盤面, 残深さ - 1 - reduction, -β, -α, 加算器深度 + 1, ct);
            // αを超えたらフル深さで再探索
            if (reduction > 0 && score > α)
                score = -Search(盤面, 残深さ - 1, -β, -α, 加算器深度 + 1, ct);

            盤面.Undo(候補手集[i], 取消);
            if (score >= β)
            {
                // 駒取りでない手のβカット → Killer & History 更新
                if (!候補手集[i].Is打ち && !盤面.Get駒(候補手集[i].Get移動先).Is有効)
                    静かな手更新(候補手集[i], 加算器深度, 残深さ);
                _置換表.保存(hash, 残深さ, β, C置換表.下限, 候補手集[i]);
                return β;
            }
            if (score > α)
            {
                α     = score;
                最善手 = 候補手集[i];
            }
        }

        if (!合法手あり) return -詰点数;

        byte フラグ = α > 元α ? C置換表.完全 : C置換表.上限;
        _置換表.保存(hash, 残深さ, α, フラグ, 最善手);
        return α;
    }
}
