using System.Numerics;
using System.Runtime.Intrinsics.X86;

namespace 変成将棋.Models;

// 81升の利き（攻撃可能升の集合）を2つのulongで表すビットボード。
// 線形インデックス = (段-1)*9 + (列-1) → 0〜80
// _下: 升0〜63（ulong下位64ビット）
// _上: 升64〜80（ulong下位17ビットのみ使用）
public readonly struct S利きビット
{
    private readonly ulong _下;
    private readonly ulong _上;

    public static readonly S利きビット 空     = new(0UL, 0UL);
    public static readonly S利きビット 全盤面 = new(ulong.MaxValue, 0x1FFFFUL); // 全81升

    // GetColKey の PEXT 用列マスク（列1〜9）
    // _下 と _上 それぞれに対して列の占有ビットを抽出するマスク
    private static readonly ulong[] _colMask下  = new ulong[10];
    private static readonly ulong[] _colMask上  = new ulong[10];
    private static readonly int[]   _col下bits  = new int[10]; // _下から抽出されるビット数（段1-8 or 段1-7）

    static S利きビット()
    {
        for (int c = 1; c <= 9; c++)
        {
            ulong m下 = 0, m上 = 0;
            int cnt下 = 0;
            for (int r = 1; r <= 9; r++)
            {
                int idx = (r - 1) * 9 + (c - 1);
                if (idx < 64) { m下 |= 1UL << idx; cnt下++; }
                else           m上 |= 1UL << (idx - 64);
            }
            _colMask下[c] = m下;
            _colMask上[c] = m上;
            _col下bits[c] = cnt下;
        }
    }

    private S利きビット(ulong 下, ulong 上)
    {
        _下 = 下;
        _上 = 上;
    }

    // 指定升が含まれているか（O(1)）
    public bool Contains(S升座標 升)
    {
        int idx = 升.線形インデックス;
        if (idx < 64) return (_下 & (1UL << idx)) != 0;
        return (_上 & (1UL << (idx - 64))) != 0;
    }

    // 指定升を追加したビットボードを返す
    public S利きビット Set(S升座標 升)
    {
        int idx = 升.線形インデックス;
        if (idx < 64) return new(_下 | (1UL << idx), _上);
        return new(_下, _上 | (1UL << (idx - 64)));
    }

    // 指定升を除去したビットボードを返す
    public S利きビット Clear(S升座標 升)
    {
        int idx = 升.線形インデックス;
        if (idx < 64) return new(_下 & ~(1UL << idx), _上);
        return new(_下, _上 & ~(1UL << (idx - 64)));
    }

    public S利きビット Or(S利きビット other)       => new(_下 | other._下, _上 | other._上);
    public S利きビット And(S利きビット other)      => new(_下 & other._下, _上 & other._上);
    public S利きビット Xor(S利きビット other)      => new(_下 ^ other._下, _上 ^ other._上);
    public S利きビット AndNot(S利きビット other)   => new(_下 & ~other._下, _上 & ~other._上);
    public bool IsEmpty => (_下 | _上) == 0;

    // 線形インデックスで直接ビットをクリア（ビットイテレーション用）
    public S利きビット ClearBit(int idx)
        => idx < 64 ? new(_下 & ~(1UL << idx), _上)
                    : new(_下, _上 & ~(1UL << (idx - 64)));

    // 静的ファクトリ：単一升からビットボードを作る
    public static S利きビット From(S升座標 升) => 空.Set(升);

    // 指定段の9bit占有キー（攻撃テーブルのルックアップキー）
    // 段 r のインデックスは (r-1)*9 〜 (r-1)*9+8 の連続9ビット
    // 段1〜7: 全て_下に収まる / 段8: bit63(_下)+bit0-7(_上) / 段9: _上のbit8-16
    public int GetRowKey(int 段)
    {
        int start = (段 - 1) * 9;
        if (start + 8 < 64)                          // 段1〜7: 全て_下
            return (int)(_下 >> start) & 0x1FF;
        if (start == 63)                              // 段8: _下bit63 + _上bit0-7
            return (int)((_下 >> 63) | (_上 << 1)) & 0x1FF;
        return (int)(_上 >> (start - 64)) & 0x1FF;   // 段9: _上bit8-16
    }

    // 指定列の9bit占有キー（PEXT命令で高速抽出、非対応CPU はループフォールバック）
    public int GetColKey(int 列)
    {
        if (Bmi2.X64.IsSupported)
        {
            int 下result = (int)Bmi2.X64.ParallelBitExtract(_下, _colMask下[列]);
            int 上result = (int)Bmi2.X64.ParallelBitExtract(_上, _colMask上[列]);
            return 下result | (上result << _col下bits[列]);
        }
        int key = 0;
        for (int 段 = 1; 段 <= 9; 段++)
            if (GetBit((段 - 1) * 9 + 列 - 1)) key |= 1 << (段 - 1);
        return key;
    }

    // 線形インデックスで直接ビットを取得（攻撃テーブル構築用）
    public bool GetBit(int linearIdx)
    {
        if (linearIdx < 64) return (_下 & (1UL << linearIdx)) != 0;
        return (_上 & (1UL << (linearIdx - 64))) != 0;
    }

    // 最小インデックス（TZCNT命令利用）。空の場合は -1
    public int FindFirstBit()
    {
        if (_下 != 0) return BitOperations.TrailingZeroCount(_下);
        if (_上 != 0) return 64 + BitOperations.TrailingZeroCount(_上);
        return -1;
    }

    // 立っているビット数（POPCNT命令利用）
    public int PopCount()
        => BitOperations.PopCount(_下) + BitOperations.PopCount(_上);

    // 最大インデックス（LZCNT命令利用）。空の場合は -1
    public int FindLastBit()
    {
        if (_上 != 0) return 64 + 63 - BitOperations.LeadingZeroCount(_上);
        if (_下 != 0) return 63 - BitOperations.LeadingZeroCount(_下);
        return -1;
    }

    // 線形インデックス 0..idx のビットを立てたマスク
    public static S利きビット BitsUpTo(int idx)
    {
        if (idx < 0)  return 空;
        if (idx >= 80) return 全盤面;
        if (idx < 64) return new(ulong.MaxValue >> (63 - idx), 0UL);
        return new(ulong.MaxValue, (1UL << (idx - 64 + 1)) - 1UL);
    }

    // 線形インデックス idx..80 のビットを立てたマスク
    public static S利きビット BitsFrom(int idx)
    {
        if (idx <= 0)  return 全盤面;
        if (idx > 80)  return 空;
        if (idx >= 64) return new(0UL, 0x1FFFFUL & (ulong.MaxValue << (idx - 64)));
        return new(ulong.MaxValue << idx, 0x1FFFFUL);
    }
}
