namespace 変成将棋.Models;

public class C盤面
{
    private readonly C升[,] 升目 = new C升[10, 10]; // 1〜9で使用、0は未使用

    public E手番 手番 { get; internal set; } = E手番.先手;
    public Dictionary<E駒種, int> 先手持ち駒 { get; } = [];
    public Dictionary<E駒種, int> 後手持ち駒 { get; } = [];

    // 利きビットボード（差分更新で維持する）
    private S利きビット _先手利き;
    private S利きビット _後手利き;
    private S利きビット _全駒ビット; // 全駒の位置（スライド利き計算に使用）
    public S利きビット 先手利き  => _先手利き;
    public S利きビット 後手利き  => _後手利き;
    public S利きビット 全駒ビット => _全駒ビット;

    public C盤面() : this(C局面設定.Load().開始局面) { }

    public C盤面(string sfen)
    {
        for (int 列 = 1; 列 <= 9; 列++)
            for (int 段 = 1; 段 <= 9; 段++)
                升目[列, 段] = new C升(列, 段);

        C将棋FE表記.Setup(this, sfen);
        _全駒ビット = Compute全駒ビット();
        (_先手利き, _後手利き) = C利き管理.Compute全利き(this);
    }

    // 盤面を初期局面に戻す（C升オブジェクトを再利用するのでVMの参照は有効なまま）
    public void Reset(string? sfen = null)
    {
        sfen ??= C局面設定.Load().開始局面;
        for (int 列 = 1; 列 <= 9; 列++)
        {
            for (int 段 = 1; 段 <= 9; 段++)
                升目[列, 段].駒 = null;
        }
        先手持ち駒.Clear();
        後手持ち駒.Clear();
        手番 = E手番.先手;
        C将棋FE表記.Setup(this, sfen);
        _全駒ビット = Compute全駒ビット();
        (_先手利き, _後手利き) = C利き管理.Compute全利き(this);
    }

    public C升    Get升(int 列, int 段)     => 升目[列, 段];
    public C駒?   Get駒(S升座標 座標)       => 升目[座標.列, 座標.段].駒;
    public C駒?   Get駒(int 列, int 段)     => 升目[列, 段].駒;
    public string ToSFEN()                  => C将棋FE表記.Serialize(this);

    internal void Set駒(int 列, int 段, E駒種 種類, E手番 手番)
        => 升目[列, 段].駒 = new C駒(種類, 手番);

    // 指定手番の玉将または獅王の位置を返す（見つからなければS升座標.なし）
    public S升座標 Find玉(E手番 手番)
    {
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 1; 列 <= 9; 列++)
            {
                var 駒 = 升目[列, 段].駒;
                if (駒?.手番 == 手番 &&
                    (駒.種類 == E駒種.玉将 || 駒.種類 == E駒種.獅王))
                {
                    return new S升座標((byte)列, (byte)段);
                }
            }
        }
        return S升座標.なし;
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

    // 手を適用して取消情報を返す。手番も切り替わる。
    public S取消情報 Apply(S手 手)
    {
        // 利き更新（盤面修正前）
        C利き管理.RemoveOld(手, this, ref _先手利き, ref _後手利き);

        C駒? 取り駒   = null;
        C駒? 中間取り駒 = null;

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            var 駒種 = 手.Get打ち駒;
            升目[先.列, 先.段].駒 = new C駒(駒種, 手番);
            Get持ち駒(手番)[駒種]--;
        }
        else if (手.Is獅王2回移動)
        {
            var 元 = new S升座標(手.移動元);
            var 中間 = new S升座標(手.中間);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[元.列, 元.段].駒!;

            // 中間升の駒を取る
            中間取り駒 = 升目[中間.列, 中間.段].駒;
            if (中間取り駒 != null)
            {
                Add持ち駒(手番, C駒.Get成り前(中間取り駒.種類));
                升目[中間.列, 中間.段].駒 = null;
            }

            // 最終升の処理（元に戻る場合は駒は動かない）
            if (先.Byte値 != 元.Byte値)
            {
                取り駒 = 升目[先.列, 先.段].駒;
                if (取り駒 != null) Add持ち駒(手番, C駒.Get成り前(取り駒.種類));
                升目[先.列, 先.段].駒 = 移動駒;
                升目[元.列, 元.段].駒 = null;
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
        }

        // 全駒ビット更新（盤面修正後・AddNew前）
        Apply全駒ビット更新(手, 中間取り駒);

        // 利き更新（盤面修正後）
        C利き管理.AddNew(手, this, ref _先手利き, ref _後手利き);

        手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;
        return new S取消情報(取り駒, 中間取り駒);
    }

    // Applyの逆操作。手番も元に戻る。
    public void Undo(S手 手, S取消情報 取消情報)
    {
        // 利き更新（盤面修正前）
        C利き管理.RemoveOld(手, this, ref _先手利き, ref _後手利き);

        手番 = 手番 == E手番.先手 ? E手番.後手 : E手番.先手;

        if (手.Is打ち)
        {
            var 先 = new S升座標(手.移動先);
            var 駒種 = 手.Get打ち駒;
            升目[先.列, 先.段].駒 = null;
            Get持ち駒(手番)[駒種]++;
        }
        else if (手.Is獅王2回移動)
        {
            var 元 = new S升座標(手.移動元);
            var 中間 = new S升座標(手.中間);
            var 先 = new S升座標(手.移動先);

            if (先.Byte値 != 元.Byte値)
            {
                // 通常の2回移動：駒を元に戻す
                var 移動駒 = 升目[先.列, 先.段].駒!;
                升目[元.列, 元.段].駒 = 移動駒;
                升目[先.列, 先.段].駒 = 取消情報.取り駒;
                if (取消情報.取り駒 != null)
                    Remove持ち駒(手番, C駒.Get成り前(取消情報.取り駒.種類));
            }
            // 元に戻る場合：移動駒は動いていないので復元不要

            if (取消情報.中間取り駒 != null)
            {
                升目[中間.列, 中間.段].駒 = 取消情報.中間取り駒;
                Remove持ち駒(手番, C駒.Get成り前(取消情報.中間取り駒.種類));
            }
        }
        else
        {
            var 元 = new S升座標(手.移動元);
            var 先 = new S升座標(手.移動先);
            var 移動駒 = 升目[先.列, 先.段].駒!;

            // 成りを取り消して元の升に戻す
            var 元種類 = 手.Is成り ? C駒.Get成り前(移動駒.種類) : 移動駒.種類;
            升目[元.列, 元.段].駒 = new C駒(元種類, 手番);
            升目[先.列, 先.段].駒 = 取消情報.取り駒;
            if (取消情報.取り駒 != null)
                Remove持ち駒(手番, C駒.Get成り前(取消情報.取り駒.種類));
        }

        // 全駒ビット更新（盤面修正後・AddNew前）
        Undo全駒ビット更新(手, 取消情報);

        // 利き更新（盤面修正後）
        C利き管理.AddNew(手, this, ref _先手利き, ref _後手利き);
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
