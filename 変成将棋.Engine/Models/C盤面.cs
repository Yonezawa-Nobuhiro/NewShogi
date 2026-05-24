namespace 変成将棋.Models;

public class C盤面
{
    private readonly C升[,] 升目 = new C升[10, 10]; // 1〜9で使用、0は未使用

    public E手番 手番 { get; internal set; } = E手番.先手;
    public int    手数 { get; private set;  } = 0;   // Apply で増加、Undo で減少
    // インデックス = (int)E駒種 (0=なし, 1〜16=各駒種)
    public int[] 先手持ち駒 { get; } = new int[17];
    public int[] 後手持ち駒 { get; } = new int[17];

    // ── Zobrist ハッシュ（ゲームループ専用・αβ探索は使用しない）──────
    // [手番(0=先手,1=後手), 駒種index(0-16), 升index(0-80)]
    private static readonly ulong[,,] _zPiece;
    // [手番(0=先手,1=後手), 駒種index(0-16), 持ち枚数index(0-17)]
    private static readonly ulong[,,] _zHand;
    private static readonly ulong _zTurn;

    // 列マスク（列1〜9: 各列の9升ビット）— Has歩兵・Generate打ちの高速化に使用
    private static readonly S利きビット[] _列マスク = new S利きビット[10];

    static C盤面()
    {
        var rng = new Random(0x5A4F3C2D);
        ulong Next() => ((ulong)(uint)rng.Next() << 32) | (uint)rng.Next();

        for (int 列 = 1; 列 <= 9; 列++)
        {
            var bits = S利きビット.空;
            for (int 段 = 1; 段 <= 9; 段++)
                bits = bits.Set(new S升座標((byte)列, (byte)段));
            _列マスク[列] = bits;
        }

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
            if (!駒.Is有効) continue;
            int p = 駒.手番 == E手番.先手 ? 0 : 1;
            h ^= _zPiece[p, (int)駒.種類, (段 - 1) * 9 + (列 - 1)];
        }
        for (int t = 1; t < 17; t++)
            for (int c = 0; c < 先手持ち駒[t]; c++) h ^= _zHand[0, t, c];
        for (int t = 1; t < 17; t++)
            for (int c = 0; c < 後手持ち駒[t]; c++) h ^= _zHand[1, t, c];
        if (手番 == E手番.後手) h ^= _zTurn;
        return h;
    }

    private S利きビット _全駒ビット; // 全駒の位置（スライド利き計算に使用）
    public S利きビット 全駒ビット => _全駒ビット;

    private ulong _αβハッシュ; // αβ探索専用Zobristハッシュ（Apply/Undoでインクリメンタル更新）
    public ulong αβハッシュ => _αβハッシュ;

    // 駒種別ビットボード（Is王手放置の高速化に使用）
    // インデックス = (int)E駒種 (0=なし, 1〜16=各駒種)
    private readonly S利きビット[] _先手駒ビット = new S利きビット[17];
    private readonly S利きビット[] _後手駒ビット = new S利きビット[17];

    public S利きビット Get駒ビット(E手番 手番, E駒種 種類)
        => 手番 == E手番.先手 ? _先手駒ビット[(int)種類] : _後手駒ビット[(int)種類];

    // 先手/後手それぞれの全駒ビット（インクリメンタル更新キャッシュ）
    private S利きビット _先手全駒ビット;
    private S利きビット _後手全駒ビット;

    // 手番側の全駒ビット合算（キャッシュから O(1) で返す）
    public S利きビット Get自駒全体ビット(E手番 手番)
        => 手番 == E手番.先手 ? _先手全駒ビット : _後手全駒ビット;

    public C盤面() : this(C局面設定.Load().開始局面) { }

    public C盤面(string sfen)
    {
        for (int 列 = 1; 列 <= 9; 列++)
            for (int 段 = 1; 段 <= 9; 段++)
                升目[列, 段] = new C升(列, 段);

        C将棋FE表記.Setup(this, sfen);
        _全駒ビット = Compute全駒ビット();
        Compute駒ビット();
        _αβハッシュ = ComputeZobristHash();
    }

    // 盤面を初期局面に戻す（C升オブジェクトを再利用するのでVMの参照は有効なまま）
    public void Reset(string? sfen = null)
    {
        sfen ??= C局面設定.Load().開始局面;
        for (int 列 = 1; 列 <= 9; 列++)
            for (int 段 = 1; 段 <= 9; 段++)
                升目[列, 段].駒 = C駒.空;
        Array.Clear(先手持ち駒, 0, 17);
        Array.Clear(後手持ち駒, 0, 17);
        手番 = E手番.先手;
        手数 = 0;
        C将棋FE表記.Setup(this, sfen);
        _全駒ビット = Compute全駒ビット();
        Compute駒ビット();
        _αβハッシュ = ComputeZobristHash();
    }

    public C升 Get升(int 列, int 段)         => 升目[列, 段];
    public C駒 Get駒(S升座標 座標)           => 升目[座標.列, 座標.段].駒;
    public C駒 Get駒(int 列, int 段)         => 升目[列, 段].駒;
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
        => !Get駒ビット(手番, E駒種.歩兵).And(_列マスク[列]).IsEmpty;

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
        Array.Copy(src.先手持ち駒, 先手持ち駒, 17);
        Array.Copy(src.後手持ち駒, 後手持ち駒, 17);
        手番 = src.手番;
        手数 = src.手数;
        _全駒ビット = src._全駒ビット;
        Array.Copy(src._先手駒ビット, _先手駒ビット, 17);
        Array.Copy(src._後手駒ビット, _後手駒ビット, 17);
        _先手全駒ビット = src._先手全駒ビット;
        _後手全駒ビット = src._後手全駒ビット;
        _αβハッシュ = src._αβハッシュ;
    }

    // Null Move（盤面変更なしで手番だけ入れ替える）
    public void ApplyNullMove()  { 手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手; _αβハッシュ ^= _zTurn; }
    public void UndoNullMove()   { 手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手; _αβハッシュ ^= _zTurn; }

    // 手を適用して取消情報を返す。手番も切り替わる。
    public S取消情報 Apply(S手 手)
    {
        C駒 取り駒   = C駒.空;
        C駒 中間取り駒 = C駒.空;

        var 自 = 手番 == E手番.先手 ? _先手駒ビット : _後手駒ビット;
        var 相手 = 手番 == E手番.先手 ? _後手駒ビット : _先手駒ビット;
        int p = 手番 == E手番.先手 ? 0 : 1;
        int 相手p = 1 - p;

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            var 駒種 = 手.Get打ち駒;
            var dict = Get持ち駒(手番);
            int c = dict[(int)駒種]; // 打ち前枚数
            _αβハッシュ ^= _zHand[p, (int)駒種, c - 1];
            dict[(int)駒種] = c - 1;
            升目[先.列, 先.段].駒 = new C駒(駒種, 手番);
            自[(int)駒種] = 自[(int)駒種].Set(先);
            _αβハッシュ ^= _zPiece[p, (int)駒種, 先.線形インデックス];
        }
        else if (手.Is獅王2回移動)
        {
            var 元 = new S升座標(手.移動元);
            var 中間 = new S升座標(手.中間);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[元.列, 元.段].駒;

            中間取り駒 = 升目[中間.列, 中間.段].駒;
            if (中間取り駒.Is有効)
            {
                _αβハッシュ ^= _zPiece[相手p, (int)中間取り駒.種類, 中間.線形インデックス];
                var 成り前中間 = C駒.Get成り前(中間取り駒.種類);
                Add持ち駒(手番, 成り前中間);
                _αβハッシュ ^= _zHand[p, (int)成り前中間, Get持ち駒(手番)[(int)成り前中間] - 1];
                升目[中間.列, 中間.段].駒 = C駒.空;
                相手[(int)中間取り駒.種類] = 相手[(int)中間取り駒.種類].Clear(中間);
            }

            if (先.Byte値 != 元.Byte値)
            {
                _αβハッシュ ^= _zPiece[p, (int)移動駒.種類, 元.線形インデックス];
                取り駒 = 升目[先.列, 先.段].駒;
                if (取り駒.Is有効)
                {
                    _αβハッシュ ^= _zPiece[相手p, (int)取り駒.種類, 先.線形インデックス];
                    var 成り前先 = C駒.Get成り前(取り駒.種類);
                    Add持ち駒(手番, 成り前先);
                    _αβハッシュ ^= _zHand[p, (int)成り前先, Get持ち駒(手番)[(int)成り前先] - 1];
                }
                升目[先.列, 先.段].駒 = 移動駒;
                升目[元.列, 元.段].駒 = C駒.空;
                自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(元).Set(先);
                if (取り駒.Is有効)
                    相手[(int)取り駒.種類] = 相手[(int)取り駒.種類].Clear(先);
                _αβハッシュ ^= _zPiece[p, (int)移動駒.種類, 先.線形インデックス];
            }
        }
        else
        {
            var 元 = new S升座標(手.移動元);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[元.列, 元.段].駒;

            _αβハッシュ ^= _zPiece[p, (int)移動駒.種類, 元.線形インデックス];
            取り駒 = 升目[先.列, 先.段].駒;
            if (取り駒.Is有効)
            {
                _αβハッシュ ^= _zPiece[相手p, (int)取り駒.種類, 先.線形インデックス];
                var 成り前種類 = C駒.Get成り前(取り駒.種類);
                Add持ち駒(手番, 成り前種類);
                _αβハッシュ ^= _zHand[p, (int)成り前種類, Get持ち駒(手番)[(int)成り前種類] - 1];
            }

            var 新種類 = 手.Is成り ? C駒.Get成り後(移動駒.種類) : 移動駒.種類;
            升目[先.列, 先.段].駒 = new C駒(新種類, 手番);
            升目[元.列, 元.段].駒 = C駒.空;
            自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(元);
            自[(int)新種類]      = 自[(int)新種類].Set(先);
            if (取り駒.Is有効)
                相手[(int)取り駒.種類] = 相手[(int)取り駒.種類].Clear(先);
            _αβハッシュ ^= _zPiece[p, (int)新種類, 先.線形インデックス];
        }

        Apply全駒ビット更新(手, 中間取り駒, 取り駒);
        手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        _αβハッシュ ^= _zTurn;
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
        int p = 手番 == E手番.先手 ? 0 : 1;
        int 相手p = 1 - p;

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            var 駒種 = 手.Get打ち駒;
            升目[先.列, 先.段].駒 = C駒.空;
            var dict = Get持ち駒(手番);
            dict[(int)駒種]++;
            int c = dict[(int)駒種]; // 戻し後枚数（=Apply前の枚数）
            _αβハッシュ ^= _zHand[p, (int)駒種, c - 1]; // Applyと同一XOR
            _αβハッシュ ^= _zPiece[p, (int)駒種, 先.線形インデックス]; // Applyと同一XOR
            自[(int)駒種] = 自[(int)駒種].Clear(先);
        }
        else if (手.Is獅王2回移動)
        {
            var 元 = new S升座標(手.移動元);
            var 中間 = new S升座標(手.中間);
            var 先 = new S升座標(手.移動先);

            if (先.Byte値 != 元.Byte値)
            {
                var 移動駒 = 升目[先.列, 先.段].駒;
                _αβハッシュ ^= _zPiece[p, (int)移動駒.種類, 先.線形インデックス];
                if (取消情報.取り駒.Is有効)
                {
                    var 成り前先 = C駒.Get成り前(取消情報.取り駒.種類);
                    int c = Get持ち駒(手番)[(int)成り前先]; // Remove前の枚数
                    _αβハッシュ ^= _zHand[p, (int)成り前先, c - 1]; // Applyと同一XOR
                    Remove持ち駒(手番, 成り前先);
                    _αβハッシュ ^= _zPiece[相手p, (int)取消情報.取り駒.種類, 先.線形インデックス];
                }
                升目[元.列, 元.段].駒 = 移動駒;
                升目[先.列, 先.段].駒 = 取消情報.取り駒;
                _αβハッシュ ^= _zPiece[p, (int)移動駒.種類, 元.線形インデックス];
                自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(先).Set(元);
                if (取消情報.取り駒.Is有効)
                    相手[(int)取消情報.取り駒.種類] = 相手[(int)取消情報.取り駒.種類].Set(先);
            }

            if (取消情報.中間取り駒.Is有効)
            {
                var 成り前中間 = C駒.Get成り前(取消情報.中間取り駒.種類);
                int c = Get持ち駒(手番)[(int)成り前中間]; // Remove前の枚数
                _αβハッシュ ^= _zHand[p, (int)成り前中間, c - 1]; // Applyと同一XOR
                Remove持ち駒(手番, 成り前中間);
                升目[中間.列, 中間.段].駒 = 取消情報.中間取り駒;
                _αβハッシュ ^= _zPiece[相手p, (int)取消情報.中間取り駒.種類, 中間.線形インデックス];
                相手[(int)取消情報.中間取り駒.種類] = 相手[(int)取消情報.中間取り駒.種類].Set(中間);
            }
        }
        else
        {
            var 元 = new S升座標(手.移動元);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[先.列, 先.段].駒;  // Apply後の先升にいる駒（成り後の可能性あり）

            _αβハッシュ ^= _zPiece[p, (int)移動駒.種類, 先.線形インデックス]; // Applyと同一XOR
            var 元種類 = 手.Is成り ? C駒.Get成り前(移動駒.種類) : 移動駒.種類;
            if (取消情報.取り駒.Is有効)
            {
                var 成り前種類 = C駒.Get成り前(取消情報.取り駒.種類);
                int c = Get持ち駒(手番)[(int)成り前種類]; // Remove前の枚数
                _αβハッシュ ^= _zHand[p, (int)成り前種類, c - 1]; // Applyと同一XOR
                Remove持ち駒(手番, 成り前種類);
                _αβハッシュ ^= _zPiece[相手p, (int)取消情報.取り駒.種類, 先.線形インデックス]; // Applyと同一XOR
            }
            升目[元.列, 元.段].駒 = new C駒(元種類, 手番);
            升目[先.列, 先.段].駒 = 取消情報.取り駒;
            _αβハッシュ ^= _zPiece[p, (int)元種類, 元.線形インデックス]; // Applyと同一XOR
            自[(int)移動駒.種類] = 自[(int)移動駒.種類].Clear(先);
            自[(int)元種類]      = 自[(int)元種類].Set(元);
            if (取消情報.取り駒.Is有効)
                相手[(int)取消情報.取り駒.種類] = 相手[(int)取消情報.取り駒.種類].Set(先);
        }

        _αβハッシュ ^= _zTurn; // 手番変更と対応するXOR
        Undo全駒ビット更新(手, 取消情報);
    }

    private int[] Get持ち駒(E手番 手番)
        => 手番 == E手番.先手 ? 先手持ち駒 : 後手持ち駒;

    private void Add持ち駒(E手番 手番, E駒種 種類)
    {
        var 持ち駒 = Get持ち駒(手番);
        持ち駒[(int)種類]++;
    }

    private void Remove持ち駒(E手番 手番, E駒種 種類)
    {
        var 持ち駒 = Get持ち駒(手番);
        持ち駒[(int)種類]--;
    }

    // ===== 駒種別ビットボード ヘルパー =====

    // 全駒種ビットボードを盤面から初期計算する
    private void Compute駒ビット()
    {
        for (int i = 0; i < 17; i++)
            _先手駒ビット[i] = _後手駒ビット[i] = S利きビット.空;
        _先手全駒ビット = _後手全駒ビット = S利きビット.空;
        for (int 段 = 1; 段 <= 9; 段++)
        for (int 列 = 1; 列 <= 9; 列++)
        {
            var 駒 = 升目[列, 段].駒;
            if (!駒.Is有効) continue;
            var 升 = new S升座標((byte)列, (byte)段);
            if (駒.手番 == E手番.先手)
            {
                _先手駒ビット[(int)駒.種類] = _先手駒ビット[(int)駒.種類].Set(升);
                _先手全駒ビット = _先手全駒ビット.Set(升);
            }
            else
            {
                _後手駒ビット[(int)駒.種類] = _後手駒ビット[(int)駒.種類].Set(升);
                _後手全駒ビット = _後手全駒ビット.Set(升);
            }
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
                if (升目[列, 段].駒.Is有効)
                    bits = bits.Set(new S升座標((byte)列, (byte)段));
            }
        }
        return bits;
    }

    // Apply後（盤面修正後）に全駒ビットと自駒全体ビットを更新する
    // 呼び出し時点では手番はまだ移動した側
    private void Apply全駒ビット更新(S手 手, C駒 中間取り駒, C駒 取り駒)
    {
        ref S利きビット 自全体 = ref (手番 == E手番.先手 ? ref _先手全駒ビット : ref _後手全駒ビット);
        ref S利きビット 相全体 = ref (手番 == E手番.先手 ? ref _後手全駒ビット : ref _先手全駒ビット);

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            _全駒ビット = _全駒ビット.Set(先);
            自全体 = 自全体.Set(先);
            return;
        }
        var 元sq = 手.Get移動元;
        var 先sq = 手.Get移動先;
        if (手.Is獅王2回移動)
        {
            if (先sq.Byte値 != 元sq.Byte値)
            {
                _全駒ビット = _全駒ビット.Clear(元sq).Set(先sq);
                自全体 = 自全体.Clear(元sq).Set(先sq);
                if (取り駒.Is有効) 相全体 = 相全体.Clear(先sq);
            }
            if (中間取り駒.Is有効)
            {
                _全駒ビット = _全駒ビット.Clear(手.Get中間);
                相全体 = 相全体.Clear(手.Get中間);
            }
        }
        else
        {
            _全駒ビット = _全駒ビット.Clear(元sq).Set(先sq);
            自全体 = 自全体.Clear(元sq).Set(先sq);
            if (取り駒.Is有効) 相全体 = 相全体.Clear(先sq);
        }
    }

    // Undo後（盤面修正後）に全駒ビットと自駒全体ビットを更新する
    // 呼び出し時点では手番は移動した側（Undoで戻し済み）
    private void Undo全駒ビット更新(S手 手, S取消情報 取消情報)
    {
        ref S利きビット 自全体 = ref (手番 == E手番.先手 ? ref _先手全駒ビット : ref _後手全駒ビット);
        ref S利きビット 相全体 = ref (手番 == E手番.先手 ? ref _後手全駒ビット : ref _先手全駒ビット);

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            _全駒ビット = _全駒ビット.Clear(先);
            自全体 = 自全体.Clear(先);
            return;
        }
        var 元sq = 手.Get移動元;
        var 先sq = 手.Get移動先;
        if (手.Is獅王2回移動)
        {
            if (先sq.Byte値 != 元sq.Byte値)
            {
                _全駒ビット = _全駒ビット.Set(元sq);
                自全体 = 自全体.Set(元sq);
                自全体 = 自全体.Clear(先sq);
                if (!取消情報.取り駒.Is有効)
                    _全駒ビット = _全駒ビット.Clear(先sq);
                else
                    相全体 = 相全体.Set(先sq);
            }
            if (取消情報.中間取り駒.Is有効)
            {
                _全駒ビット = _全駒ビット.Set(手.Get中間);
                相全体 = 相全体.Set(手.Get中間);
            }
        }
        else
        {
            _全駒ビット = _全駒ビット.Set(元sq);
            自全体 = 自全体.Set(元sq);
            自全体 = 自全体.Clear(先sq);
            if (!取消情報.取り駒.Is有効)
                _全駒ビット = _全駒ビット.Clear(先sq);
            else
                相全体 = 相全体.Set(先sq);
        }
    }
}
