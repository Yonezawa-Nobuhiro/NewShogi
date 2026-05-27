using 変成将棋.Models;

namespace 変成将棋.AI.αβAI;

public struct C置換表エントリ
{
    public uint ハッシュ上位;  // ulong ハッシュの上位 32bit（衝突検出）
    public int  スコア;
    public byte 深さ;
    public byte フラグ;        // 0=完全, 1=下限(βカット), 2=上限(αに届かず)
    public byte 世代;          // 探索ごとにインクリメント（古いエントリを上書き可能にする）
    public S手  最善手;
}

public sealed class C置換表
{
    internal const byte 完全 = 0, 下限 = 1, 上限 = 2;

    private readonly C置換表エントリ[] _表;
    private readonly ulong _マスク;
    private byte _現在世代;

    internal C置換表(int 対数 = 23)   // 2^23 = 8Mエントリ ≒ 112MB
    {
        _表   = new C置換表エントリ[1 << 対数];
        _マスク = (ulong)(_表.Length - 1);
    }

    // 探索開始時に呼ぶ。世代が変わることで古いエントリを上書き可能にする。
    internal void 世代を進める() => _現在世代++;

    // ヒットかつ十分な深さなら true とスコアを返す。最善手は同世代・ハッシュ一致時のみ返す（手順並べ替え用）。
    internal bool 検索(ulong h, int 深さ, int α, int β, out int スコア, out S手 最善手)
    {
        ref var e = ref _表[h & _マスク];
        スコア  = 0;
        最善手  = default;
        if (e.ハッシュ上位 != (uint)(h >> 32)) return false;
        if (e.世代 != _現在世代)               return false;  // 古い世代はスコア・最善手ともに使わない
        最善手 = e.最善手;
        if (e.深さ < 深さ)                     return false;
        スコア = e.スコア;
        if (e.フラグ == 完全)              return true;
        if (e.フラグ == 下限 && スコア >= β) return true;
        if (e.フラグ == 上限 && スコア <= α) return true;
        return false;
    }

    internal void 保存(ulong h, int 深さ, int スコア, byte フラグ, S手 最善手)
    {
        ref var e = ref _表[h & _マスク];
        // 同じ局面・同じ世代でより深い完全スコアがあれば保護。古い世代は常に上書き可能。
        if (e.ハッシュ上位 == (uint)(h >> 32) && e.世代 == _現在世代 && e.深さ > 深さ && e.フラグ == 完全) return;
        e.ハッシュ上位 = (uint)(h >> 32);
        e.スコア       = スコア;
        e.深さ         = (byte)Math.Min(深さ, 255);
        e.フラグ        = フラグ;
        e.世代         = _現在世代;
        e.最善手        = 最善手;
    }
}
