using 変成将棋.Models;

namespace 変成将棋.Engine.Tests;

/// <summary>
/// C攻撃テーブル（マジックビットボード）の正確性テスト。
/// 各スライド駒・固定駒のテーブル参照結果をブルートフォースと比較する。
/// </summary>
public class C攻撃テーブルTests
{
    // ─── ユーティリティ ──────────────────────────────────────────────

    private static S升座標 Sq(int 列, int 段) => new((byte)列, (byte)段);

    /// <summary>
    /// ブルートフォースで水平スライドを計算する（テーブルとの比較用）
    /// </summary>
    private static S利きビット 水平ブルートフォース(S升座標 sq, S利きビット 全駒)
    {
        var result = S利きビット.空;
        for (int c = sq.列 + 1; c <= 9; c++)
        {
            var t = Sq(c, sq.段);
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        for (int c = sq.列 - 1; c >= 1; c--)
        {
            var t = Sq(c, sq.段);
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        return result;
    }

    private static S利きビット 垂直ブルートフォース(S升座標 sq, S利きビット 全駒)
    {
        var result = S利きビット.空;
        for (int r = sq.段 + 1; r <= 9; r++)
        {
            var t = Sq(sq.列, r);
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        for (int r = sq.段 - 1; r >= 1; r--)
        {
            var t = Sq(sq.列, r);
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        return result;
    }

    private static S利きビット 右斜めブルートフォース(S升座標 sq, S利きビット 全駒)
    {
        var result = S利きビット.空;
        for (int d = 1; d <= 8; d++) // 段増加=列減少
        {
            var t = sq.Add(-d, d);
            if (!t.Is有効) break;
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        for (int d = 1; d <= 8; d++) // 段減少=列増加
        {
            var t = sq.Add(d, -d);
            if (!t.Is有効) break;
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        return result;
    }

    private static S利きビット 左斜めブルートフォース(S升座標 sq, S利きビット 全駒)
    {
        var result = S利きビット.空;
        for (int d = 1; d <= 8; d++) // 段増加=列増加
        {
            var t = sq.Add(d, d);
            if (!t.Is有効) break;
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        for (int d = 1; d <= 8; d++) // 段減少=列減少
        {
            var t = sq.Add(-d, -d);
            if (!t.Is有効) break;
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        return result;
    }

    private static S利きビット 香車先手ブルートフォース(S升座標 sq, S利きビット 全駒)
    {
        var result = S利きビット.空;
        for (int r = sq.段 - 1; r >= 1; r--)
        {
            var t = Sq(sq.列, r);
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        return result;
    }

    private static S利きビット 香車後手ブルートフォース(S升座標 sq, S利きビット 全駒)
    {
        var result = S利きビット.空;
        for (int r = sq.段 + 1; r <= 9; r++)
        {
            var t = Sq(sq.列, r);
            result = result.Set(t);
            if (全駒.Contains(t)) break;
        }
        return result;
    }

    /// <summary>ブルートフォースと一致しているか確認する（ビット単位の比較）</summary>
    private static bool IsEqual(S利きビット a, S利きビット b)
        => a.Xor(b).IsEmpty;

    // ─── 水平スライドテスト ──────────────────────────────────────────

    [Fact]
    public void 水平_ブロッカーなし_全マスを攻撃()
    {
        var sq = Sq(5, 5);
        var 全駒 = S利きビット.From(sq); // 自分自身のみ
        var テーブル = C攻撃テーブル.飛車(sq, 全駒).And(S利きビット.From(sq).Xor(S利きビット.全盤面)); // sq自身を除外
        var ブルート = 水平ブルートフォース(sq, 全駒).Or(垂直ブルートフォース(sq, 全駒));
        Assert.True(IsEqual(C攻撃テーブル.飛車(sq, 全駒), ブルート));
    }

    [Fact]
    public void 水平_両側ブロッカー_ブロッカーまで攻撃()
    {
        var sq = Sq(5, 5);
        var 左ブロッカー = Sq(3, 5);
        var 右ブロッカー = Sq(7, 5);
        var 全駒 = S利きビット.From(sq).Set(左ブロッカー).Set(右ブロッカー);

        var expected = 水平ブルートフォース(sq, 全駒);
        var actual = C攻撃テーブル.飛車(sq, 全駒).And(
            // 水平成分だけ取り出す（同一段）
            BuildRow(5));
        Assert.True(IsEqual(expected, actual));
    }

    [Fact]
    public void 水平_全升でテーブルがブルートフォースと一致()
    {
        // 全81升×いくつかの代表占有パターンで検証
        var パターン = new[] { S利きビット.空, S利きビット.全盤面 };
        foreach (var (sq列, sq段) in AllSquares())
        {
            var sq = Sq(sq列, sq段);
            foreach (var 全駒 in パターン)
            {
                var expected = 水平ブルートフォース(sq, 全駒);
                // 飛車テーブルの水平成分 = 同一段のみ
                var actual = C攻撃テーブル.飛車(sq, 全駒).And(BuildRow(sq段));
                Assert.True(IsEqual(expected, actual),
                    $"水平不一致 at ({sq列},{sq段}) occ={全駒.IsEmpty}");
            }
        }
    }

    // ─── 垂直スライドテスト ──────────────────────────────────────────

    [Fact]
    public void 垂直_全升でテーブルがブルートフォースと一致()
    {
        var パターン = new[] { S利きビット.空, S利きビット.全盤面 };
        foreach (var (sq列, sq段) in AllSquares())
        {
            var sq = Sq(sq列, sq段);
            foreach (var 全駒 in パターン)
            {
                var expected = 垂直ブルートフォース(sq, 全駒);
                var actual = C攻撃テーブル.飛車垂直(sq, 全駒);
                Assert.True(IsEqual(expected, actual),
                    $"垂直不一致 at ({sq列},{sq段})");
            }
        }
    }

    [Fact]
    public void 垂直_中央から上下ブロッカー()
    {
        var sq = Sq(5, 5);
        var 上ブロッカー = Sq(5, 3); // 段3（先手方向）
        var 下ブロッカー = Sq(5, 7); // 段7（後手方向）
        var 全駒 = S利きビット.From(sq).Set(上ブロッカー).Set(下ブロッカー);

        var expected = 垂直ブルートフォース(sq, 全駒);
        var actual = C攻撃テーブル.飛車垂直(sq, 全駒);

        Assert.True(IsEqual(expected, actual));
        // ブロッカー升が含まれていることを確認
        Assert.True(actual.Contains(上ブロッカー));
        Assert.True(actual.Contains(下ブロッカー));
        // ブロッカーの外側は含まれていないことを確認
        Assert.False(actual.Contains(Sq(5, 1)));
        Assert.False(actual.Contains(Sq(5, 9)));
    }

    // ─── 対角スライドテスト ──────────────────────────────────────────

    [Fact]
    public void 角行_ランダム位置でブルートフォースと一致()
    {
        var テスト升 = new[] { Sq(1,1), Sq(9,9), Sq(5,5), Sq(1,9), Sq(9,1), Sq(3,7) };
        var 全駒 = S利きビット.空;

        foreach (var sq in テスト升)
        {
            var expected = 右斜めブルートフォース(sq, 全駒).Or(左斜めブルートフォース(sq, 全駒));
            var actual = C攻撃テーブル.角行(sq, 全駒);
            Assert.True(IsEqual(expected, actual),
                $"角行不一致 at ({sq.列},{sq.段})");
        }
    }

    [Fact]
    public void 角行_ブロッカーありでブルートフォースと一致()
    {
        var sq = Sq(5, 5);
        var ブロッカー = Sq(3, 3); // 左斜め方向
        var 全駒 = S利きビット.From(sq).Set(ブロッカー);

        var expected = 右斜めブルートフォース(sq, 全駒).Or(左斜めブルートフォース(sq, 全駒));
        var actual = C攻撃テーブル.角行(sq, 全駒);

        Assert.True(IsEqual(expected, actual));
        Assert.True(actual.Contains(ブロッカー));       // ブロッカー升は含む
        Assert.False(actual.Contains(Sq(1, 1)));       // その先は含まない
    }

    // ─── 香車テスト ──────────────────────────────────────────────────

    [Fact]
    public void 香車先手_段5から段1まで攻撃()
    {
        var sq = Sq(5, 5);
        var 全駒 = S利きビット.From(sq);
        var actual = C攻撃テーブル.香車先手(sq, 全駒);

        // 段1〜4が攻撃範囲（ブロッカーなし）
        for (int r = 1; r <= 4; r++)
            Assert.True(actual.Contains(Sq(5, r)), $"香車先手: 段{r}が含まれていない");
        // 段6〜9は攻撃しない
        for (int r = 6; r <= 9; r++)
            Assert.False(actual.Contains(Sq(5, r)), $"香車先手: 段{r}が含まれている（誤）");
    }

    [Fact]
    public void 香車先手_ブロッカーで停止()
    {
        var sq = Sq(5, 6);
        var ブロッカー = Sq(5, 4);
        var 全駒 = S利きビット.From(sq).Set(ブロッカー);
        var actual = C攻撃テーブル.香車先手(sq, 全駒);

        Assert.True(actual.Contains(Sq(5, 5)));    // ブロッカー手前
        Assert.True(actual.Contains(ブロッカー));   // ブロッカー自身
        Assert.False(actual.Contains(Sq(5, 3)));   // ブロッカー先は攻撃しない
        Assert.False(actual.Contains(Sq(5, 1)));
    }

    [Fact]
    public void 香車後手_段5から段9まで攻撃()
    {
        var sq = Sq(5, 5);
        var 全駒 = S利きビット.From(sq);
        var actual = C攻撃テーブル.香車後手(sq, 全駒);

        for (int r = 6; r <= 9; r++)
            Assert.True(actual.Contains(Sq(5, r)), $"香車後手: 段{r}が含まれていない");
        for (int r = 1; r <= 4; r++)
            Assert.False(actual.Contains(Sq(5, r)), $"香車後手: 段{r}が含まれている（誤）");
    }

    [Fact]
    public void 香車_全升でブルートフォースと一致()
    {
        foreach (var (sq列, sq段) in AllSquares())
        {
            var sq = Sq(sq列, sq段);
            // ブロッカーなし
            var 全駒 = S利きビット.From(sq);
            Assert.True(IsEqual(C攻撃テーブル.香車先手(sq, 全駒), 香車先手ブルートフォース(sq, 全駒)),
                $"香車先手不一致 at ({sq列},{sq段})");
            Assert.True(IsEqual(C攻撃テーブル.香車後手(sq, 全駒), 香車後手ブルートフォース(sq, 全駒)),
                $"香車後手不一致 at ({sq列},{sq段})");
        }
    }

    // ─── 獅王テスト ──────────────────────────────────────────────────

    [Fact]
    public void 獅王_中央からチェビシェフ距離2の全マスを攻撃()
    {
        var sq = Sq(5, 5);
        var actual = C攻撃テーブル.獅王(sq);

        // 距離1の8マス
        int[] d1 = [-1, 0, 1];
        foreach (int dr in d1)
        foreach (int dc in d1)
        {
            if (dr == 0 && dc == 0) continue;
            var t = sq.Add(dc, dr);
            if (t.Is有効) Assert.True(actual.Contains(t), $"獅王: 距離1 ({dc},{dr})が含まれていない");
        }

        // 距離2のマス（チェビシェフ）
        for (int dr = -2; dr <= 2; dr++)
        for (int dc = -2; dc <= 2; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            if (Math.Abs(dr) <= 1 && Math.Abs(dc) <= 1) continue; // 距離1は上で確認済み
            var t = sq.Add(dc, dr);
            if (t.Is有効) Assert.True(actual.Contains(t), $"獅王: 距離2 ({dc},{dr})が含まれていない");
        }

        // 距離3以上は攻撃しない
        var t3 = sq.Add(0, 3);
        if (t3.Is有効) Assert.False(actual.Contains(t3), "獅王: 距離3が含まれている（誤）");
    }

    [Fact]
    public void 獅王_隅から盤内のみ攻撃()
    {
        var sq = Sq(1, 1); // 左上隅
        var actual = C攻撃テーブル.獅王(sq);

        // (1,1)から距離2以内で盤内のマスのみ含む
        int expectedCount = 0;
        for (int dr = -2; dr <= 2; dr++)
        for (int dc = -2; dc <= 2; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            var t = sq.Add(dc, dr);
            if (t.Is有効)
            {
                Assert.True(actual.Contains(t));
                expectedCount++;
            }
        }
        // ビット数が期待数と一致
        int count = CountBits(actual);
        Assert.Equal(expectedCount, count);
    }

    // ─── 固定駒テスト ────────────────────────────────────────────────

    [Fact]
    public void 固定_歩兵先手_一段前のみ()
    {
        var sq = Sq(5, 5);
        var actual = C攻撃テーブル.固定(E駒種.歩兵, E手番.先手, sq);

        Assert.True(actual.Contains(Sq(5, 4)));   // 一段前（先手なので段減少）
        Assert.False(actual.Contains(Sq(5, 6)));  // 一段後ろ
        Assert.Equal(1, CountBits(actual));
    }

    [Fact]
    public void 固定_歩兵後手_一段前のみ()
    {
        var sq = Sq(5, 5);
        var actual = C攻撃テーブル.固定(E駒種.歩兵, E手番.後手, sq);

        Assert.True(actual.Contains(Sq(5, 6)));   // 一段前（後手なので段増加）
        Assert.False(actual.Contains(Sq(5, 4)));
        Assert.Equal(1, CountBits(actual));
    }

    [Fact]
    public void 固定_桂馬先手_2升前の左右()
    {
        var sq = Sq(5, 5);
        var actual = C攻撃テーブル.固定(E駒種.桂馬, E手番.先手, sq);

        Assert.True(actual.Contains(Sq(4, 3)));  // 2段前+右（列減少）
        Assert.True(actual.Contains(Sq(6, 3)));  // 2段前+左（列増加）
        Assert.Equal(2, CountBits(actual));
    }

    [Fact]
    public void 固定_金将先手_6方向()
    {
        var sq = Sq(5, 5);
        var actual = C攻撃テーブル.固定(E駒種.金将, E手番.先手, sq);

        // 金将：前・後・左・右・左前・右前の6マス
        Assert.True(actual.Contains(Sq(5, 4)));  // 前
        Assert.True(actual.Contains(Sq(5, 6)));  // 後
        Assert.True(actual.Contains(Sq(4, 5)));  // 右
        Assert.True(actual.Contains(Sq(6, 5)));  // 左
        Assert.True(actual.Contains(Sq(6, 4)));  // 左前
        Assert.True(actual.Contains(Sq(4, 4)));  // 右前
        Assert.False(actual.Contains(Sq(4, 6))); // 右後（金将は行けない）
        Assert.False(actual.Contains(Sq(6, 6))); // 左後（金将は行けない）
        Assert.Equal(6, CountBits(actual));
    }

    // ─── GetRow占有・GetCol占有テスト ────────────────────────────────

    [Fact]
    public void GetRow占有_段5に3駒でキーが正しい()
    {
        var 全駒 = S利きビット.空.Set(Sq(1, 5)).Set(Sq(5, 5)).Set(Sq(9, 5));
        int key = C攻撃テーブル.GetRow占有(全駒, 5);

        // bit0=列1, bit4=列5, bit8=列9 が立っている
        Assert.Equal(1 | (1 << 4) | (1 << 8), key);
    }

    [Fact]
    public void GetCol占有_列5に3駒でキーが正しい()
    {
        var 全駒 = S利きビット.空.Set(Sq(5, 1)).Set(Sq(5, 5)).Set(Sq(5, 9));
        int key = C攻撃テーブル.GetCol占有(全駒, 5);

        // bit0=段1, bit4=段5, bit8=段9 が立っている
        Assert.Equal(1 | (1 << 4) | (1 << 8), key);
    }

    // ─── ヘルパー ────────────────────────────────────────────────────

    private static IEnumerable<(int, int)> AllSquares()
    {
        for (int 列 = 1; 列 <= 9; 列++)
        for (int 段 = 1; 段 <= 9; 段++)
            yield return (列, 段);
    }

    private static S利きビット BuildRow(int 段)
    {
        var bits = S利きビット.空;
        for (int 列 = 1; 列 <= 9; 列++)
            bits = bits.Set(new S升座標((byte)列, (byte)段));
        return bits;
    }

    private static int CountBits(S利きビット bits)
    {
        int count = 0;
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
            if (bits.Contains(new S升座標((byte)列, (byte)段))) count++;
        return count;
    }
}
