namespace 変成将棋.Models;

public class C盤面
{
    private readonly C升[,] 升目 = new C升[10, 10]; // 1〜9で使用、0は未使用

    public E手番 手番 { get; internal set; } = E手番.先手;
    public int    手数 { get; private set;  } = 0;   // Apply で増加、Undo で減少
    public Dictionary<E駒種, int> 先手持ち駒 { get; } = [];
    public Dictionary<E駒種, int> 後手持ち駒 { get; } = [];

    // ── Zobrist ハッシュ（ゲームループ専用・αβ探索は使用しない）──────
    // [手番(0=先手,1=後手), 駒種index(0-16), 升index(0-80)]
    private static readonly ulong[,,] _zPiece;
    // [手番(0=先手,1=後手), 駒種index(0-16), 持ち枚数index(0-17)]
    private static readonly ulong[,,] _zHand;
    private static readonly ulong _zTurn;

    static C盤面()
    {
        var rng = new Random(0x5A4F3C2D);
        ulong Next() => ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();

        _zPiece = new ulong[2, 17, 81];
        for (int p = 0; p < 2; p++)
        for (int t = 0; t < 17; t++)
        for (int s = 0; s < 81; s++)
            _zPiece[p, t, s] = Next();

        _zHand = new ulong[2, 17, 18];
        for (int p = 0; p < 2; p++)
        for (int t = 0; t < 17; t++)
        for (int c = 0; c < 18; c++)
            _zHand[p, t, c] = Next();

        _zTurn = Next();
    }

    // 盤面全体から Zobrist ハッシュを計算して返す。
    // Apply/Undo でインクリメンタル更新は行わない（αβ 側に負荷をかけないため）。
    // ゲームループ（C対局VM）が1手ごとに呼び出す。
    public ulong ComputeZobristHash()
    {
        ulong h = 0;
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 升目[列, 段].駒;
            if (駒 == null) continue;
            int p = 駒.手番 == E手番.先手 ? 0 : 1;
            h ^= _zPiece[p, (int)駒.種類, (段 - 1) * 9 + (列 - 1)];
        }
        foreach (var (種類, 枚数) in 先手持ち駒)
            for (int c = 0; c < 枚数; c++) h ^= _zHand[0, (int)種類, c];
        foreach (var (種類, 枚数) in 後手持ち駒)
            for (int c = 0; c < 枚数; c++) h ^= _zHand[1, (int)種類, c];
        if (手番 == E手番.後手) h ^= _zTurn;
        return h;
    }

    private S利きビット _全駒ビット; // 全駒の位置（スライド利き計算に使用）
    public S利きビット 全駒ビット => _全駒ビット;

    // 駒種別ビットボード（Is王手放置の高速化に使用）
    // インデックス = (int)E駒種 (0=なし, 1〜16=各駒種)
    private readonly S利きビット[] _先手駒ビット = new S利きビット[17];
    private readonly S利きビット[] _後手駒ビット = new S利きビット[17];

    public S利きビット Get駒ビット(E手番 手番, E駒種 種類)
        => 手番 == E手番.先手 ? _先手駒ビット[(int)種類] : _後手駒ビット[(int)種類];

    public C盤面() : this(C局面設定.Load().開始局面) { }

    public C盤面(string sfen)
    {
        for (int 列 = 1; 列 <= 9; 列++)
            for (int 段 = 1; 段 <= 9; 段++)
                升目[列, 段] = new C升(列, 段);

        C将棋FE表記.Setup(this, sfen);
        _全駒ビット = Compute全駒ビット();
        Compute駒ビット();
    }

    // 盤面を初期局面に戻す（C升オブジェクトを再利用するのでVMの参照は有効なまま）
    public void Reset(string? sfen = null)
    {
        sfen ??= C局面設定.Load().開始局面;
        for (int 列 = 1; 列 <= 9; 列++)
            for (int 段 = 1; 段 <= 9; 段++)
                升目[列, 段].駒 = null;
        先手持ち駒.Clear();
        後手持ち駒.Clear();
        手番 = E手番.先手;
        手数 = 0;
        C将棋FE表記.Setup(this, sfen);
        _全駒ビット = Compute全駒ビット();
        Compute駒ビット();
    }

    public C升    Get升(int 列, int 段)     => 升目[列, 段];
    public C駒?   Get駒(S升座標 座標)       => 升目[座標.列, 座標.段].駒;
    public C駒?   Get駒(int 列, int 段)     => 升目[列, 段].駒;
    public string ToSFEN()                  => C将棋FE表記.Serialize(this);

    internal void Set駒(int 列, int 段, E駒種 種類, E手番 手番)
        => 升目[列, 段].駒 = new C駒(種類, 手番);

    // 指定手番の玉将または獅王の位置を返す（見つからなければS升座標.なし）
    // 駒種別ビットボードを使って O(1) で検索する（TZCNT命令）
    public S升座標 Find玉(E手番 手番)
    {
        var bits = Get駒ビット(手番, E駒種.玉将).Or(Get駒ビット(手番, E駒種.獅王));
        int idx = bits.FindFirstBit();
        if (idx < 0) return S升座標.なし;
        return new S升座標((byte)((idx % 9) + 1), (byte)((idx / 9) + 1));
    }

    // 指定手番の指定列に歩兵が存在するか（二歩チェック用）
    public bool Has歩兵(E手番 手番, int 列)
    {
        for (int 段 = 1; 段 <= 9; 段++)
        {
            var 駒 = 升目[列, 段].駒;
            if (駒?.手番 == 手番 && 駒.種類 == E駒種.歩兵) return true;
        }
        return false;
    }

    // 局面を src からコピー（SFEN経由より高速）
    public void CopyFrom(C盤面 src)
    {
        for (int 列 = 1; 列 <= 9; 列++)
        {
            for (int 段 = 1; 段 <= 9; 段++)
            {
                升目[列, 段].駒 = src.升目[列, 段].駒;
            }
        }
        先手持ち駒.Clear();
        foreach (var kv in src.先手持ち駒) 先手持ち駒[kv.Key] = kv.Value;
        後手持ち駒.Clear();
        foreach (var kv in src.後手持ち駒) 後手持ち駒[kv.Key] = kv.Value;
        手番 = src.手番;
        手数 = src.手数;
        _全駒ビット = src._全駒ビット;
        Array.Copy(src._先手駒ビット, _先手駒ビット, 17);
        Array.Copy(src._後手駒ビット, _後手駒ビット, 17);
    }

    // Null Move（盤面変更なしで手番だけ入れ替える）
    public void ApplyNullMove()  => 手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;
    public void UndoNullMove()   => 手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;

    // 手を適用して取消情報を返す。手番も切り替わる。
    public S取消情報 Apply(S手 手)
    {
        C駒? 取り駒   = null;
        C駒? 中間取り駒 = null;

        var 自 = 手番 == E手番.先手 ? _先手駒ビット : _後手駒ビット;
        var 相手 = 手番 == E手番.先手 ? _後手駒ビット : _先手駒ビット;

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            var 駒種 = 手.Get打ち駒;
            升目[先.列, 先.段].駒 = new C駒(駒種, 手番);
            Get持ち駒(手番)[駒種]--;
            自[(int)駒種] = 自[(int)駒種].Set(先);
        }
        else if (手.Is獅王2回移動)
        {
            var 元 = new S升座標(手.移動元);
            var 中間 = new S升座標(手.中間);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[元.列, 元.段].駒!;

            中間取り駒 = 升目[中間.列, 中間.段].駒;
            if (中間取り駒 != null)
            {
                Add持ち駒(手番, C駒.Get成り前(中間取り駒.種類));
                升目[中間.列, 中間.段].駒 = null;
                相手[(int)中間取り駒.種類] = 相手[(int)中間取り駒.種類].Clear(中間);
            }

            if (先.Byte値 != 元.Byte値)
            {
                取り駒 = 升目[先.列, 先.段].駒;
                if (取り駒 != null) Add持ち駒(手番, C駒.Get成り前(取り駒.種類));
                升目[先.列, 先.段].駒 = 移動駒;
                升目[元.列, 元.段].駒 = null;
                自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(元).Set(先);
                if (取り駒 != null)
                    相手[(int)取り駒.種類] = 相手[(int)取り駒.種類].Clear(先);
            }
        }
        else
        {
            var 元 = new S升座標(手.移動元);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[元.列, 元.段].駒!;

            取り駒 = 升目[先.列, 先.段].駒;
            if (取り駒 != null) Add持ち駒(手番, C駒.Get成り前(取り駒.種類));

            var 新種類 = 手.Is成り ? C駒.Get成り後(移動駒.種類) : 移動駒.種類;
            升目[先.列, 先.段].駒 = new C駒(新種類, 手番);
            升目[元.列, 元.段].駒 = null;
            自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(元);
            自[(int)新種類]      = 自[(int)新種類].Set(先);
            if (取り駒 != null)
                相手[(int)取り駒.種類] = 相手[(int)取り駒.種類].Clear(先);
        }

        Apply全駒ビット更新(手, 中間取り駒);
        手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        手数++;
        return new S取消情報(取り駒, 中間取り駒);
    }

    // Applyの逆操作。手番も元に戻る。
    public void Undo(S手 手, S取消情報 取消情報)
    {
        手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        手数--;

        var 自 = 手番 == E手番.先手 ? _先手駒ビット : _後手駒ビット;
        var 相手 = 手番 == E手番.先手 ? _後手駒ビット : _先手駒ビット;

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            var 駒種 = 手.Get打ち駒;
            升目[先.列, 先.段].駒 = null;
            Get持ち駒(手番)[駒種]++;

            // 駒ビット差分更新：打ち駒を除去
            自[(int)駒種] = 自[(int)駒種].Clear(先);
        }
        else if (手.Is獅王2回移動)
        {
            var 元 = new S升座標(手.移動元);
            var 中間 = new S升座標(手.中間);
            var 先 = new S升座標(手.移動先);

            if (先.Byte値 != 元.Byte値)
            {
                var 移動駒 = 升目[先.列, 先.段].駒!;
                升目[元.列, 元.段].駒 = 移動駒;
                升目[先.列, 先.段].駒 = 取消情報.取り駒;
                if (取消情報.取り駒 != null)
                    Remove持ち駒(手番, C駒.Get成り前(取消情報.取り駒.種類));

                // 駒ビット差分更新：移動先→移動元に戻す、取り駒を復元
                自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(先).Set(元);
                if (取消情報.取り駒 != null)
                    相手[(int)取消情報.取り駒.種類] = 相手[(int)取消情報.取り駒.種類].Set(先);
            }

            if (取消情報.中間取り駒 != null)
            {
                升目[中間.列, 中間.段].駒 = 取消情報.中間取り駒;
                Remove持ち駒(手番, C駒.Get成り前(取消情報.中間取り駒.種類));
                相手[(int)取消情報.中間取り駒.種類] = 相手[(int)取消情報.中間取り駒.種類].Set(中間);
            }
        }
        else
        {
            var 元 = new S升座標(手.移動元);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[先.列, 先.段].駒!;  // Apply後の先升にいる駒（成り後の可能性あり）

            var 元種類 = 手.Is成り ? C駒.Get成り前(移動駒.種類) : 移動駒.種類;
            升目[元.列, 元.段].駒 = new C駒(元種類, 手番);
            升目[先.列, 先.段].駒 = 取消情報.取り駒;
            if (取消情報.取り駒 != null)
                Remove持ち駒(手番, C駒.Get成り前(取消情報.取り駒.種類));

            // 駒ビット差分更新：成り後種類を先から除去、元種類を元に復元、取り駒を復元
            自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(先);
            自[(int)元種類]      = 自[(int)元種類].Set(元);
            if (取消情報.取り駒 != null)
                相手[(int)取消情報.取り駒.種類] = 相手[(int)取消情報.取り駒.種類].Set(先);
        }

        Undo全駒ビット更新(手, 取消情報);
    }

    private Dictionary<E駒種, int> Get持ち駒(E手番 手番)
        => 手番 == E手番.先手 ? 先手持ち駒 : 後手持ち駒;

    private void Add持ち駒(E手番 手番, E駒種 種類)
    {
        var 持ち駒 = Get持ち駒(手番);
        持ち駒[種類] = 持ち駒.GetValueOrDefault(種類) + 1;
    }

    private void Remove持ち駒(E手番 手番, E駒種 種類)
    {
        var 持ち駒 = Get持ち駒(手番);
        持ち駒[種類]--;
    }

    // ===== 駒種別ビットボード ヘルパー =====

    // 全駒種ビットボードを盤面から初期計算する
    private void Compute駒ビット()
    {
        for (int i = 0; i < 17; i++)
            _先手駒ビット[i] = _後手駒ビット[i] = S利きビット.空;
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 升目[列, 段].駒;
            if (駒 == null) continue;
            var 升 = new S升座標((byte)列, (byte)段);
            var bits = 駒.手番 == E手番.先手 ? _先手駒ビット : _後手駒ビット;
            bits[(int)駒.種類] = bits[(int)駒.種類].Set(升);
        }
    }

    // ===== 全駒ビット更新ヘルパー =====

    // 全駒の位置ビットボードを初期計算する
    private S利きビット Compute全駒ビット()
    {
        var bits = S利きビット.空;
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                if (升目[列, 段].駒 != null)
                    bits = bits.Set(new S升座標((byte)列, (byte)段));
            }
        }
        return bits;
    }

    // Apply後（盤面修正後）に全駒ビットを更新する
    private void Apply全駒ビット更新(S手 手, C駒? 中間取り駒)
    {
        if (手.Is打ち)
        {
            _全駒ビット = _全駒ビット.Set(new S升座標(手.移動先));
            return;
        }
        var 元 = 手.Get移動元;
        var 先 = 手.Get移動先;
        if (手.Is獅王2回移動)
        {
            if (先.Byte値 != 元.Byte値) _全駒ビット = _全駒ビット.Clear(元).Set(先);
            if (中間取り駒 != null)      _全駒ビット = _全駒ビット.Clear(手.Get中間);
        }
        else
        {
            _全駒ビット = _全駒ビット.Clear(元).Set(先);
        }
    }

    // Undo後（盤面修正後）に全駒ビットを更新する
    private void Undo全駒ビット更新(S手 手, S取消情報 取消情報)
    {
        if (手.Is打ち)
        {
            _全駒ビット = _全駒ビット.Clear(new S升座標(手.移動先));
            return;
        }
        var 元 = 手.Get移動元;
        var 先 = 手.Get移動先;
        if (手.Is獅王2回移動)
        {
            if (先.Byte値 != 元.Byte値)
            {
                _全駒ビット = _全駒ビット.Set(元);
                if (取消情報.取り駒 == null) _全駒ビット = _全駒ビット.Clear(先);
            }
            if (取消情報.中間取り駒 != null) _全駒ビット = _全駒ビット.Set(手.Get中間);
        }
        else
        {
            _全駒ビット = _全駒ビット.Set(元);
            if (取消情報.取り駒 == null) _全駒ビット = _全駒ビット.Clear(先);
        }
    }
}
