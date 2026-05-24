namespace 変成将棋.Models;

/// <summary>
/// 各升・各駒種の攻撃ビットボードを高速に取得するための事前計算テーブル。
///
/// スライド駒（飛車・角行・香車・竪行縦）は全駒ビットに応じた攻撃範囲を
/// 9bit 占有キーで引くテーブルにより O(1) で取得する（マジックビットボード相当）。
///
/// 固定駒（歩・桂・銀・金・と金・騎兵・麒麟・鳳凰・龍馬固定・龍王固定・玉将）と
/// 獅王はチェビシェフ距離2以内すべてを固定テーブルで管理する。
/// </summary>
public static class C攻撃テーブル
{
    // ─── スライド攻撃テーブル ────────────────────────────────────────
    // [sq:81][row_or_col_occ_9bit:512]
    private static readonly S利きビット[,] _水平  = new S利きビット[81, 512];
    private static readonly S利きビット[,] _垂直  = new S利きビット[81, 512];
    // 右斜め: 列+段=const 方向（左上→右下）
    private static readonly S利きビット[,] _右斜め = new S利きビット[81, 512];
    // 左斜め: 列-段=const 方向（右上→左下）
    private static readonly S利きビット[,] _左斜め = new S利きビット[81, 512];
    // 香車専用：前方向のみ / 後方向のみ
    private static readonly S利きビット[,] _香先手 = new S利きビット[81, 512];
    private static readonly S利きビット[,] _香後手 = new S利きビット[81, 512];

    // ─── 固定駒テーブル ──────────────────────────────────────────────
    // [piece:17][color:2 (先=0,後=1)][sq:81]
    private static readonly S利きビット[,,] _固定 = new S利きビット[17, 2, 81];

    // ─── 獅王テーブル ────────────────────────────────────────────────
    // チェビシェフ距離2以内すべて（占有無関係、固定）
    private static readonly S利きビット[] _獅王 = new S利きビット[81];

    // ─── 初期化 ──────────────────────────────────────────────────────
    static C攻撃テーブル()
    {
        Init水平();
        Init垂直();
        Init斜め();
        Init香();
        Init固定();
        Init獅王();
    }

    // ─── 公開 API ────────────────────────────────────────────────────

    /// <summary>飛車の攻撃ビット（水平＋垂直）</summary>
    public static S利きビット 飛車(S升座標 sq, S利きビット 全駒)
    {
        int idx = sq.線形インデックス;
        return _水平[idx, GetRow占有(全駒, sq.段)]
              .Or(_垂直[idx, GetCol占有(全駒, sq.列)]);
    }

    /// <summary>飛車の垂直方向のみ（竪行スライド部分に流用）</summary>
    public static S利きビット 飛車垂直(S升座標 sq, S利きビット 全駒)
        => _垂直[sq.線形インデックス, GetCol占有(全駒, sq.列)];

    /// <summary>角行の攻撃ビット（両対角）</summary>
    public static S利きビット 角行(S升座標 sq, S利きビット 全駒)
    {
        int idx = sq.線形インデックス;
        return _右斜め[idx, GetRight斜め占有(全駒, sq)]
              .Or(_左斜め[idx, GetLeft斜め占有(全駒, sq)]);
    }

    /// <summary>先手香車の攻撃ビット（前方向＝段減少）</summary>
    public static S利きビット 香車先手(S升座標 sq, S利きビット 全駒)
        => _香先手[sq.線形インデックス, GetCol占有(全駒, sq.列)];

    /// <summary>後手香車の攻撃ビット（前方向＝段増加）</summary>
    public static S利きビット 香車後手(S升座標 sq, S利きビット 全駒)
        => _香後手[sq.線形インデックス, GetCol占有(全駒, sq.列)];

    /// <summary>固定移動駒の攻撃ビット（スライドなし駒に使用）</summary>
    public static S利きビット 固定(E駒種 種類, E手番 手番, S升座標 sq)
        => _固定[(int)種類, 手番 == E手番.先手 ? 0 : 1, sq.線形インデックス];

    /// <summary>獅王の攻撃ビット（チェビシェフ距離2以内すべて）</summary>
    public static S利きビット 獅王(S升座標 sq) => _獅王[sq.線形インデックス];

    // ─── 占有キー抽出ヘルパー ─────────────────────────────────────────

    // 指定段の9bit占有キー（シフト＋マスクで O(1)）
    public static int GetRow占有(S利きビット bits, int 段) => bits.GetRowKey(段);

    // 指定列の9bit占有キー（PEXT命令で O(1)）
    public static int GetCol占有(S利きビット bits, int 列) => bits.GetColKey(列);

    // 右斜め（列+段=const）の占有キー
    // 同じ対角上の升を段の昇順で bit0 から並べる
    private static int GetRight斜め占有(S利きビット bits, S升座標 sq)
    {
        int key = 0, bit = 0;
        for (int 段 = 1; 段 <= 9; 段++)
        {
            int 列 = sq.列 + sq.段 - 段; // 列+段=const → 列 = const - 段
            if (列 < 1 || 列 > 9) continue;
            if (bits.GetBit((段 - 1) * 9 + 列 - 1)) key |= 1 << bit;
            bit++;
        }
        return key;
    }

    // 左斜め（列-段=const）の占有キー
    private static int GetLeft斜め占有(S利きビット bits, S升座標 sq)
    {
        int key = 0, bit = 0;
        for (int 段 = 1; 段 <= 9; 段++)
        {
            int 列 = sq.列 - sq.段 + 段; // 列-段=const → 列 = const + 段
            if (列 < 1 || 列 > 9) continue;
            if (bits.GetBit((段 - 1) * 9 + 列 - 1)) key |= 1 << bit;
            bit++;
        }
        return key;
    }

    // ─── テーブル初期化 ──────────────────────────────────────────────

    private static void Init水平()
    {
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            int sq = (段 - 1) * 9 + (列 - 1);
            for (int occ = 0; occ < 512; occ++)
            {
                var attack = S利きビット.空;
                for (int c = 列 + 1; c <= 9; c++) // 列増加方向
                {
                    attack = attack.Set(new S升座標((byte)c, (byte)段));
                    if ((occ >> (c - 1) & 1) != 0) break;
                }
                for (int c = 列 - 1; c >= 1; c--) // 列減少方向
                {
                    attack = attack.Set(new S升座標((byte)c, (byte)段));
                    if ((occ >> (c - 1) & 1) != 0) break;
                }
                _水平[sq, occ] = attack;
            }
        }
    }

    private static void Init垂直()
    {
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            int sq = (段 - 1) * 9 + (列 - 1);
            for (int occ = 0; occ < 512; occ++)
            {
                var attack = S利きビット.空;
                for (int r = 段 + 1; r <= 9; r++) // 段増加方向
                {
                    attack = attack.Set(new S升座標((byte)列, (byte)r));
                    if ((occ >> (r - 1) & 1) != 0) break;
                }
                for (int r = 段 - 1; r >= 1; r--) // 段減少方向
                {
                    attack = attack.Set(new S升座標((byte)列, (byte)r));
                    if ((occ >> (r - 1) & 1) != 0) break;
                }
                _垂直[sq, occ] = attack;
            }
        }
    }

    private static void Init斜め()
    {
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            int sq = (段 - 1) * 9 + (列 - 1);
            var 升 = new S升座標((byte)列, (byte)段);

            // 右斜め占有キーの全パターン
            // 同対角上の升を収集（段の昇順）
            var diag右 = new List<S升座標>();
            for (int r = 1; r <= 9; r++)
            {
                int c = 列 + 段 - r;
                if (c >= 1 && c <= 9) diag右.Add(new S升座標((byte)c, (byte)r));
            }
            int maxKey右 = 1 << diag右.Count;
            for (int occ = 0; occ < maxKey右; occ++)
            {
                // 全駒ビットを再構築
                var 全駒 = S利きビット.空;
                for (int i = 0; i < diag右.Count; i++)
                    if ((occ >> i & 1) != 0) 全駒 = 全駒.Set(diag右[i]);

                var attack = S利きビット.空;
                // 段増加方向（列減少）
                for (int r = 段 + 1; r <= 9; r++)
                {
                    int c = 列 + 段 - r;
                    if (c < 1 || c > 9) break;
                    var t = new S升座標((byte)c, (byte)r);
                    attack = attack.Set(t);
                    if (全駒.Contains(t)) break;
                }
                // 段減少方向（列増加）
                for (int r = 段 - 1; r >= 1; r--)
                {
                    int c = 列 + 段 - r;
                    if (c < 1 || c > 9) break;
                    var t = new S升座標((byte)c, (byte)r);
                    attack = attack.Set(t);
                    if (全駒.Contains(t)) break;
                }
                // キーをGetRight斜め占有と同じ方式で再計算
                int key = GetRight斜め占有(全駒, 升);
                _右斜め[sq, key] = attack;
            }

            // 左斜め
            var diag左 = new List<S升座標>();
            for (int r = 1; r <= 9; r++)
            {
                int c = 列 - 段 + r;
                if (c >= 1 && c <= 9) diag左.Add(new S升座標((byte)c, (byte)r));
            }
            int maxKey左 = 1 << diag左.Count;
            for (int occ = 0; occ < maxKey左; occ++)
            {
                var 全駒 = S利きビット.空;
                for (int i = 0; i < diag左.Count; i++)
                    if ((occ >> i & 1) != 0) 全駒 = 全駒.Set(diag左[i]);

                var attack = S利きビット.空;
                // 段増加方向（列増加）
                for (int r = 段 + 1; r <= 9; r++)
                {
                    int c = 列 - 段 + r;
                    if (c < 1 || c > 9) break;
                    var t = new S升座標((byte)c, (byte)r);
                    attack = attack.Set(t);
                    if (全駒.Contains(t)) break;
                }
                // 段減少方向（列減少）
                for (int r = 段 - 1; r >= 1; r--)
                {
                    int c = 列 - 段 + r;
                    if (c < 1 || c > 9) break;
                    var t = new S升座標((byte)c, (byte)r);
                    attack = attack.Set(t);
                    if (全駒.Contains(t)) break;
                }
                int key = GetLeft斜め占有(全駒, 升);
                _左斜め[sq, key] = attack;
            }
        }
    }

    private static void Init香()
    {
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            int sq = (段 - 1) * 9 + (列 - 1);
            for (int occ = 0; occ < 512; occ++)
            {
                var 先 = S利きビット.空;
                var 後 = S利きビット.空;
                for (int r = 段 - 1; r >= 1; r--) // 先手香: 段減少
                {
                    先 = 先.Set(new S升座標((byte)列, (byte)r));
                    if ((occ >> (r - 1) & 1) != 0) break;
                }
                for (int r = 段 + 1; r <= 9; r++) // 後手香: 段増加
                {
                    後 = 後.Set(new S升座標((byte)列, (byte)r));
                    if ((occ >> (r - 1) & 1) != 0) break;
                }
                _香先手[sq, occ] = 先;
                _香後手[sq, occ] = 後;
            }
        }
    }

    private static void Init固定()
    {
        // Get到達升テーブルを使って全固定駒の攻撃テーブルを構築
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 升 = new S升座標((byte)列, (byte)段);
            int sq = 升.線形インデックス;

            foreach (E手番 手番 in new[] { E手番.先手, E手番.後手 })
            {
                int ci = 手番 == E手番.先手 ? 0 : 1;
                for (int t = 1; t <= 16; t++)
                {
                    var 種類 = (E駒種)t;
                    // スライド+固定の複合駒は固定部分のみ（Get到達升はFixed部分を返す）
                    // 純スライド駒（香・角・飛）は到達升がないので空のまま
                    var attack = S利きビット.空;
                    foreach (byte b in C到達升テーブル.Get到達升(種類, 手番, 升))
                        attack = attack.Set(new S升座標(b));
                    _固定[t, ci, sq] = attack;
                }
            }
        }
    }

    private static void Init獅王()
    {
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 升 = new S升座標((byte)列, (byte)段);
            int sq = 升.線形インデックス;
            var attack = S利きビット.空;
            // チェビシェフ距離2以内のすべてのマス（タイプA+Bで常に到達可能）
            for (int dr = -2; dr <= 2; dr++)
            for (int dc = -2; dc <= 2; dc++)
            {
                if (dr == 0 && dc == 0) continue;
                var t = 升.Add(dc, dr);
                if (t.Is有効) attack = attack.Set(t);
            }
            _獅王[sq] = attack;
        }
    }
}
