using static 変成将棋.Models.C駒動き定数;
namespace 変成将棋.Models;

// 合法手生成器。
// Generate擬似合法手で王手放置を除く全手を生成し、Filter王手放置で絞り込む。
// stackalloc S手[最大手数] のバッファを渡して使用する。
public static class C合法手生成器
{
    public const int 最大手数 = 600;


    // 合法手を生成してバッファに書き込み、手数を返す
    public static int Get合法手(C盤面 盤面, Span<S手> バッファ)
    {
        int 手数 = Generate擬似合法手(盤面, バッファ);
        手数 = Filter王手放置(盤面, バッファ, 手数);
        手数 = Filter打歩詰(盤面, バッファ, 手数);
        return 手数;
    }

    // 手を指した後（Apply後）に指した側の玉が安全かどうか判定する。
    // αβ探索の内部ループで王手放置チェックに使用する（王手放置なら false）。
    public static bool Is自玉安全(C盤面 盤面, E手番 指した側)
        => !Is王手放置(盤面, 指した側);

    // 王手放置チェックなしの擬似合法手を生成する
    public static int Generate擬似合法手(C盤面 盤面, Span<S手> バッファ)
    {
        int 手数 = 0;
        var 手番 = 盤面.手番;

        // 盤上の駒の移動
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 升 = new S升座標((byte)列, (byte)段);
                var 駒 = 盤面.Get駒(升);
                if (駒 is null || 駒.手番 != 手番) continue;
                手数 += Generate駒移動(盤面, 升, 駒.種類, 手番, バッファ[手数..]);
            }
        }

        // 持ち駒の打ち
        手数 += Generate打ち(盤面, 手番, バッファ[手数..]);
        return 手数;
    }

    // Quiescence Search 専用: 駒取り手のみを生成する（持ち駒打ちは除く）
    public static int Generate駒取り手(C盤面 盤面, Span<S手> バッファ)
    {
        int 手数 = 0;
        var 手番 = 盤面.手番;
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 升 = new S升座標((byte)列, (byte)段);
            var 駒 = 盤面.Get駒(升);
            if (駒 is null || 駒.手番 != 手番) continue;
            手数 += Generate駒取り移動(盤面, 升, 駒.種類, 手番, バッファ[手数..]);
        }
        return 手数;
    }

    private static int Generate駒取り移動(
        C盤面 盤面, S升座標 元, E駒種 種類, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        switch (種類)
        {
            case E駒種.香車:
                手数 += Add駒取りスライド(盤面, 元, 種類, 手番,
                    手番 == E手番.先手 ? 香車先手スライド : 香車後手スライド, バッファ);
                break;
            case E駒種.角行:
                手数 += Add駒取りスライド(盤面, 元, 種類, 手番, 角行スライド, バッファ);
                break;
            case E駒種.飛車:
                手数 += Add駒取りスライド(盤面, 元, 種類, 手番, 飛車スライド, バッファ);
                break;
            case E駒種.竪行:
                手数 += Add駒取りスライド(盤面, 元, 種類, 手番, 竪行スライド, バッファ);
                手数 += Add駒取り固定(盤面, 元, 種類, 手番, バッファ[手数..]);
                break;
            case E駒種.龍馬:
                手数 += Add駒取りスライド(盤面, 元, 種類, 手番, 角行スライド, バッファ);
                手数 += Add駒取り固定(盤面, 元, 種類, 手番, バッファ[手数..]);
                break;
            case E駒種.龍王:
                手数 += Add駒取りスライド(盤面, 元, 種類, 手番, 飛車スライド, バッファ);
                手数 += Add駒取り固定(盤面, 元, 種類, 手番, バッファ[手数..]);
                break;
            case E駒種.獅王:
                手数 += Generate獅王駒取り移動(盤面, 元, 手番, バッファ);
                break;
            case E駒種.玉将:
                手数 += Add玉将駒取り移動(盤面, 元, 手番, バッファ);
                break;
            default:
                手数 += Add駒取り固定(盤面, 元, 種類, 手番, バッファ);
                break;
        }
        return 手数;
    }

    // スライド先で最初に当たった敵駒のみ追加
    private static int Add駒取りスライド(
        C盤面 盤面, S升座標 元, E駒種 種類, E手番 手番, int[] 方向一覧, Span<S手> バッファ)
    {
        int 手数 = 0;
        foreach (int 方向 in 方向一覧)
        {
            foreach (byte 移動先Byte in C到達升テーブル.Getスライドレイ(方向, 元))
            {
                var 先 = new S升座標(移動先Byte);
                var 先駒 = 盤面.Get駒(先);
                if (先駒?.手番 == 手番) break;        // 自駒でブロック
                if (先駒!= null)                       // 敵駒 = 駒取り手を追加
                {
                    Add手または両手(種類, 手番, 元, 先, バッファ, ref 手数);
                    break;
                }
                // 空升はスキップ（続行）
            }
        }
        return 手数;
    }

    // 移動先が敵駒の手のみ追加
    private static int Add駒取り固定(
        C盤面 盤面, S升座標 元, E駒種 種類, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        foreach (byte 移動先Byte in C到達升テーブル.Get到達升(種類, 手番, 元))
        {
            var 先 = new S升座標(移動先Byte);
            var 先駒 = 盤面.Get駒(先);
            if (先駒== null || 先駒.手番 == 手番) continue; // 空升・自駒はスキップ
            Add手または両手(種類, 手番, 元, 先, バッファ, ref 手数);
        }
        return 手数;
    }

    // 獅王の駒取り手のみを生成
    private static int Generate獅王駒取り移動(
        C盤面 盤面, S升座標 元, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        var 隣接升一覧 = C到達升テーブル.Get獅王隣接(元);

        foreach (byte 中間Byte in 隣接升一覧)
        {
            var 中間 = new S升座標(中間Byte);
            var 中間駒 = 盤面.Get駒(中間);
            if (中間駒?.手番 == 手番) continue; // 自駒には通過不可

            // 1回移動: 中間升に敵駒がある場合のみ生成
            if (中間駒!= null)
                バッファ[手数++] = S手.Create通常(元, 中間);

            // 2回移動: 中間を素通り（または中間取り）して先升へ
            foreach (byte 先Byte in C到達升テーブル.Get獅王隣接(中間))
            {
                var 先 = new S升座標(先Byte);
                if (先.Byte値 == 元.Byte値) continue; // 元に戻る＋取りなし
                if (盤面.Get駒(先)?.手番 == 手番) continue;
                var 先駒 = 盤面.Get駒(先);
                // 中間取り または 先取り のどちらかが発生する場合のみ
                if (中間駒!= null || 先駒!= null)
                    バッファ[手数++] = S手.Create獅王2回移動(元, 中間, 先);
            }
        }

        // タイプB: チェビシェフ距離2ジャンプで敵駒を取る
        foreach (byte 先Byte in C到達升テーブル.Get獅王遠達(元))
        {
            var 先 = new S升座標(先Byte);
            var 先駒 = 盤面.Get駒(先);
            if (先駒== null || 先駒.手番 == 手番) continue;
            バッファ[手数++] = S手.Create通常(元, 先);
        }

        return 手数;
    }

    // 玉将の駒取り手のみ生成
    private static int Add玉将駒取り移動(C盤面 盤面, S升座標 元, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        foreach (byte b in C到達升テーブル.Get到達升(E駒種.玉将, 手番, 元))
        {
            var 先 = new S升座標(b);
            var 先駒 = 盤面.Get駒(先);
            if (先駒== null || 先駒.手番 == 手番) continue; // 空升・自駒はスキップ

            if (Is玉将成り可能(盤面, 手番, 元, 先))
            {
                バッファ[手数++] = S手.Create通常(元, 先, 成り: true);
                バッファ[手数++] = S手.Create通常(元, 先, 成り: false);
            }
            else
            {
                バッファ[手数++] = S手.Create通常(元, 先, 成り: false);
            }
        }
        return 手数;
    }

    // ===== 駒種別の移動生成 =====

    private static int Generate駒移動(
        C盤面 盤面, S升座標 元, E駒種 種類, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;

        switch (種類)
        {
            // 純スライド駒
            case E駒種.香車:
                手数 += Addスライド(盤面, 元, 種類, 手番,
                    手番 == E手番.先手 ? 香車先手スライド : 香車後手スライド, バッファ);
                break;
            case E駒種.角行:
                手数 += Addスライド(盤面, 元, 種類, 手番, 角行スライド, バッファ);
                break;
            case E駒種.飛車:
                手数 += Addスライド(盤面, 元, 種類, 手番, 飛車スライド, バッファ);
                break;

            // スライド+固定の複合駒
            case E駒種.竪行:
                手数 += Addスライド(盤面, 元, 種類, 手番, 竪行スライド, バッファ);
                手数 += Add固定(盤面, 元, 種類, 手番, バッファ[手数..]);
                break;
            case E駒種.龍馬:
                手数 += Addスライド(盤面, 元, 種類, 手番, 角行スライド, バッファ);
                手数 += Add固定(盤面, 元, 種類, 手番, バッファ[手数..]);
                break;
            case E駒種.龍王:
                手数 += Addスライド(盤面, 元, 種類, 手番, 飛車スライド, バッファ);
                手数 += Add固定(盤面, 元, 種類, 手番, バッファ[手数..]);
                break;

            // 獅王（特殊移動）
            case E駒種.獅王:
                手数 += Generate獅王移動(盤面, 元, 手番, バッファ);
                break;

            // 玉将（特別な成り条件あり）
            case E駒種.玉将:
                手数 += Add玉将移動(盤面, 元, 手番, バッファ);
                break;

            // 固定移動駒（テーブル参照）
            default:
                手数 += Add固定(盤面, 元, 種類, 手番, バッファ);
                break;
        }

        return 手数;
    }

    // スライド移動の生成（指定方向の升を順に走査し、駒にぶつかるまで追加）
    private static int Addスライド(
        C盤面 盤面, S升座標 元, E駒種 種類, E手番 手番, int[] 方向一覧, Span<S手> バッファ)
    {
        int 手数 = 0;
        foreach (int 方向 in 方向一覧)
        {
            foreach (byte 移動先Byte in C到達升テーブル.Getスライドレイ(方向, 元))
            {
                var 先 = new S升座標(移動先Byte);
                var 先駒 = 盤面.Get駒(先);
                if (先駒?.手番 == 手番) break; // 自駒でブロック
                Add手または両手(種類, 手番, 元, 先, バッファ, ref 手数);
                if (先駒!= null) break; // 相手駒を取ったら止まる
            }
        }
        return 手数;
    }

    // 固定移動の生成（テーブルから到達升を取得）
    private static int Add固定(
        C盤面 盤面, S升座標 元, E駒種 種類, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        foreach (byte 移動先Byte in C到達升テーブル.Get到達升(種類, 手番, 元))
        {
            var 先 = new S升座標(移動先Byte);
            if (盤面.Get駒(先)?.手番 == 手番) continue; // 自駒は除外
            Add手または両手(種類, 手番, 元, 先, バッファ, ref 手数);
        }
        return 手数;
    }

    // 獅王の移動生成（タイプA: 1〜2回移動、タイプB: チェビシェフ距離2ジャンプ）
    private static int Generate獅王移動(
        C盤面 盤面, S升座標 元, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        var 隣接升一覧 = C到達升テーブル.Get獅王隣接(元);

        // タイプA: 1回移動
        foreach (byte 中間Byte in 隣接升一覧)
        {
            var 中間 = new S升座標(中間Byte);
            var 中間駒 = 盤面.Get駒(中間);
            if (中間駒?.手番 == 手番) continue; // 自駒には移動不可

            // 1回で止まる
            バッファ[手数++] = S手.Create通常(元, 中間);

            // 2回移動（中間升に自駒がある場合は通過不可なのでスキップ済み）
            foreach (byte 先Byte in C到達升テーブル.Get獅王隣接(中間))
            {
                var 先 = new S升座標(先Byte);
                // 元のマスに戻る場合は獅王自身がいるが合法（パス扱い）
                if (先.Byte値 != 元.Byte値 && 盤面.Get駒(先)?.手番 == 手番) continue;
                バッファ[手数++] = S手.Create獅王2回移動(元, 中間, 先);
            }
        }

        // タイプB: チェビシェフ距離2へのジャンプ
        foreach (byte 先Byte in C到達升テーブル.Get獅王遠達(元))
        {
            var 先 = new S升座標(先Byte);
            if (盤面.Get駒(先)?.手番 == 手番) continue;
            バッファ[手数++] = S手.Create通常(元, 先);
        }

        return 手数;
    }

    // ===== 駒打ち =====

    private static int Generate打ち(C盤面 盤面, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        var 持ち駒 = 手番 == E手番.先手 ? 盤面.先手持ち駒 : 盤面.後手持ち駒;

        foreach (var (駒種, 枚数) in 持ち駒)
        {
            if (枚数 <= 0) continue;
            for (int 段 = 1; 段 <= 9; 段++)
            {
                for (int 列 = 1; 列 <= 9; 列++)
                {
                    if (盤面.Get駒(列, 段) != null) continue; // 空升のみ
                    if (!Is打ち可能(盤面, 駒種, 手番, 列, 段)) continue;
                    var 先 = new S升座標((byte)列, (byte)段);
                    バッファ[手数++] = S手.Create打ち(駒種, 先);
                }
            }
        }
        return 手数;
    }

    private static bool Is打ち可能(C盤面 盤面, E駒種 種類, E手番 手番, int 列, int 段)
    {
        // 行き所のない駒：歩・香は最終段不可、桂馬は最終2段不可
        if (手番 == E手番.先手)
        {
            if ((種類 == E駒種.歩兵 || 種類 == E駒種.香車) && 段 == 1) return false;
            if (種類 == E駒種.桂馬 && 段 <= 2) return false;
        }
        else
        {
            if ((種類 == E駒種.歩兵 || 種類 == E駒種.香車) && 段 == 9) return false;
            if (種類 == E駒種.桂馬 && 段 >= 8) return false;
        }

        // 二歩：同じ列に自分の歩兵がある場合は打てない
        if (種類 == E駒種.歩兵 && 盤面.Has歩兵(手番, 列)) return false;

        // TODO: 打ち歩詰め判定

        return true;
    }

    // ===== 成り判定 =====

    private static bool Is敵陣(E手番 手番, S升座標 升)
        => 手番 == E手番.先手 ? 升.段 <= 3 : 升.段 >= 7;

    // 成り義務：その升に移動すると行き所がなくなる場合は成り強制
    private static bool Is成り義務(E駒種 種類, E手番 手番, S升座標 先)
    {
        if (手番 == E手番.先手)
        {
            if (種類 == E駒種.歩兵 || 種類 == E駒種.香車) return 先.段 == 1;
            if (種類 == E駒種.桂馬) return 先.段 <= 2;
        }
        else
        {
            if (種類 == E駒種.歩兵 || 種類 == E駒種.香車) return 先.段 == 9;
            if (種類 == E駒種.桂馬) return 先.段 >= 8;
        }
        return false;
    }

    // 成り可能な移動に対して、成り手と不成り手の両方（または成り義務の場合は成りのみ）を追加
    private static void Add手または両手(
        E駒種 種類, E手番 手番, S升座標 元, S升座標 先, Span<S手> バッファ, ref int 手数)
    {
        bool 成れる = C駒.Is成れる(種類);
        bool 敵陣 = Is敵陣(手番, 元) || Is敵陣(手番, 先);

        if (成れる && 敵陣)
        {
            バッファ[手数++] = S手.Create通常(元, 先, 成り: true);
            if (!Is成り義務(種類, 手番, 先))
                バッファ[手数++] = S手.Create通常(元, 先, 成り: false);
        }
        else
        {
            バッファ[手数++] = S手.Create通常(元, 先, 成り: false);
        }
    }

    // 玉将の移動手生成（特別な成り条件を考慮）
    private static int Add玉将移動(C盤面 盤面, S升座標 元, E手番 手番, Span<S手> バッファ)
    {
        int 手数 = 0;
        foreach (byte b in C到達升テーブル.Get到達升(E駒種.玉将, 手番, 元))
        {
            var 先 = new S升座標(b);
            if (盤面.Get駒(先)?.手番 == 手番) continue;

            if (Is玉将成り可能(盤面, 手番, 元, 先))
            {
                バッファ[手数++] = S手.Create通常(元, 先, 成り: true);
                バッファ[手数++] = S手.Create通常(元, 先, 成り: false);
            }
            else
            {
                バッファ[手数++] = S手.Create通常(元, 先, 成り: false);
            }
        }
        return 手数;
    }

    // 玉将の成り条件判定
    // 条件1: 移動元または移動先が敵陣三段目以内
    // 条件2: 相手の玉将（獅王でない）が自陣三段目以内にいる
    private static bool Is玉将成り可能(C盤面 盤面, E手番 手番, S升座標 元, S升座標 先)
    {
        if (!Is敵陣(手番, 元) && !Is敵陣(手番, 先)) return false;

        var 相手手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        var 相手玉位置 = 盤面.Find玉(相手手番);
        if (!相手玉位置.Is有効) return false;

        // 相手が既に獅王なら成れない
        if (盤面.Get駒(相手玉位置)?.種類 == E駒種.獅王) return false;

        // 相手玉が自陣三段目以内（= 相手から見た敵陣三段目以内）
        return Is敵陣(相手手番, 相手玉位置);
    }

    // ===== 打歩詰フィルタ =====
    // 歩を打って相手玉が詰みになる手（打歩詰）は非合法なので除外する。
    // 内部で Generate擬似合法手 を使用し Get合法手 への再帰を避ける。

    private static int Filter打歩詰(C盤面 盤面, Span<S手> バッファ, int 手数)
    {
        var 手番 = 盤面.手番;
        var 相手 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;

        // 打歩で玉に利く唯一の升を特定（玉位置から逆算）
        var 玉 = 盤面.Find玉(相手);
        if (!玉.Is有効) return 手数;
        int 打歩段 = 手番 == E手番.先手 ? 玉.段 + 1 : 玉.段 - 1;
        if (打歩段 < 1 || 打歩段 > 9) return 手数;

        // バッファ内でその升への歩打ちを探す（二歩制約より高々1手）
        int 対象 = -1;
        for (int i = 0; i < 手数; i++)
        {
            var 手 = バッファ[i];
            if (手.Is打ち && 手.Get打ち駒 == E駒種.歩兵)
            {
                var 先 = 手.Get移動先;
                if (先.列 == 玉.列 && 先.段 == 打歩段) { 対象 = i; break; }
            }
        }
        if (対象 < 0) return 手数;  // 打歩詰候補なし

        // 打った後に詰みなら除外（バッファの該当インデックスを詰める）
        var 取消 = 盤面.Apply(バッファ[対象]);
        bool は詰み = Is擬似詰み(盤面, 相手);
        盤面.Undo(バッファ[対象], 取消);

        if (!は詰み) return 手数;
        for (int i = 対象; i < 手数 - 1; i++) バッファ[i] = バッファ[i + 1];
        return 手数 - 1;
    }

    // Generate擬似合法手 + Is自玉安全 で詰みを判定（Get合法手を呼ばないので再帰なし）
    private static bool Is擬似詰み(C盤面 盤面, E手番 手番)
    {
        Span<S手> buf = stackalloc S手[最大手数];
        int n = Generate擬似合法手(盤面, buf);
        for (int i = 0; i < n; i++)
        {
            var 取消 = 盤面.Apply(buf[i]);
            bool 安全 = Is自玉安全(盤面, 手番);
            盤面.Undo(buf[i], 取消);
            if (安全) return false;
        }
        return true;
    }

    // ===== 王手放置フィルタ =====

    private static int Filter王手放置(C盤面 盤面, Span<S手> バッファ, int 手数)
    {
        var 指す側 = 盤面.手番;
        int 有効手数 = 0;
        for (int i = 0; i < 手数; i++)
        {
            var 手 = バッファ[i];
            var 取消情報 = 盤面.Apply(手);
            bool 放置 = Is王手放置(盤面, 指す側);
            盤面.Undo(手, 取消情報);
            if (!放置) バッファ[有効手数++] = 手;
        }
        return 有効手数;
    }

    // 手を指した後（盤面.Apply後）、指した側の玉が相手の攻撃下にあるか判定。
    // 玉位置から各駒種の攻撃を逆引きし、相手駒ビットと AND を取ることで
    // O(駒種数) で判定する（マジックビットボード利用）。
    private static bool Is王手放置(C盤面 盤面, E手番 指す側)
    {
        var 玉 = 盤面.Find玉(指す側);
        if (!玉.Is有効) return true;

        var 相手 = 指す側 == E手番.先手 ? E手番.後手 : E手番.先手;
        var 全駒 = 盤面.全駒ビット;

        // ── スライド駒の逆引き ────────────────────────────────────────

        // 飛車・龍王（水平＋垂直）
        var 飛 = C攻撃テーブル.飛車(玉, 全駒);
        if (!飛.And(盤面.Get駒ビット(相手, E駒種.飛車)
                   .Or(盤面.Get駒ビット(相手, E駒種.龍王))).IsEmpty)
            return true;

        // 角行・龍馬（対角）
        var 角 = C攻撃テーブル.角行(玉, 全駒);
        if (!角.And(盤面.Get駒ビット(相手, E駒種.角行)
                   .Or(盤面.Get駒ビット(相手, E駒種.龍馬))).IsEmpty)
            return true;

        // 香車（逆引き：後手香は先手玉の上方（段小）から来る → 玉から段減少方向を確認）
        var 香 = 指す側 == E手番.先手
            ? C攻撃テーブル.香車先手(玉, 全駒)  // 先手玉：後手香は段小方向から → 先手香テーブル（段減少）で逆引き
            : C攻撃テーブル.香車後手(玉, 全駒);  // 後手玉：先手香は段大方向から → 後手香テーブル（段増加）で逆引き
        if (!香.And(盤面.Get駒ビット(相手, E駒種.香車)).IsEmpty)
            return true;

        // 竪行（縦スライド＋横固定）
        var 竪縦 = C攻撃テーブル.飛車垂直(玉, 全駒);
        var 竪横 = C攻撃テーブル.固定(E駒種.竪行, 指す側, 玉); // 逆引き: 指す側で固定部分を算出
        if (!竪縦.Or(竪横).And(盤面.Get駒ビット(相手, E駒種.竪行)).IsEmpty)
            return true;

        // ── 固定駒の逆引き（逆引き = 玉から相手視点で到達できる位置） ──
        // 原理: 相手駒TがKを攻撃 ⟺ 玉Kからの指す側T攻撃範囲に相手Tがいる

        // 龍馬の固定部分（直交1マス）
        var 龍馬固定 = C攻撃テーブル.固定(E駒種.龍馬, 指す側, 玉);
        if (!龍馬固定.And(盤面.Get駒ビット(相手, E駒種.龍馬)).IsEmpty)
            return true;

        // 龍王の固定部分（斜め1マス）
        var 龍王固定 = C攻撃テーブル.固定(E駒種.龍王, 指す側, 玉);
        if (!龍王固定.And(盤面.Get駒ビット(相手, E駒種.龍王)).IsEmpty)
            return true;

        // 純固定駒（歩・桂・銀・金・と金・騎兵・麒麟・鳳凰・玉将）
        E駒種[] 固定駒種 = [E駒種.歩兵, E駒種.桂馬, E駒種.銀将, E駒種.金将,
                           E駒種.と金, E駒種.騎兵, E駒種.麒麟, E駒種.鳳凰, E駒種.玉将];
        foreach (var 種類 in 固定駒種)
        {
            var 到達 = C攻撃テーブル.固定(種類, 指す側, 玉);
            if (!到達.And(盤面.Get駒ビット(相手, 種類)).IsEmpty)
                return true;
        }

        // 獅王（チェビシェフ距離2以内すべて）
        if (!C攻撃テーブル.獅王(玉).And(盤面.Get駒ビット(相手, E駒種.獅王)).IsEmpty)
            return true;

        return false;
    }
}
