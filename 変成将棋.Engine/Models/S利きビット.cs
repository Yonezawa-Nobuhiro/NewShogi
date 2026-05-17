using System.Numerics;

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

    public S利きビット Or(S利きビット other)  => new(_下 | other._下, _上 | other._上);
    public S利きビット And(S利きビット other) => new(_下 & other._下, _上 & other._上);
    public S利きビット Xor(S利きビット other) => new(_下 ^ other._下, _上 ^ other._上);
    public bool IsEmpty => (_下 | _上) == 0;

    // 静的ファクトリ：単一升からビットボードを作る
    public static S利きビット From(S升座標 升) => 空.Set(升);

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
