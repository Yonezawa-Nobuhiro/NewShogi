using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using 変成将棋.AI;
using 変成将棋.Models;
using static 変成将棋.Models.C合法手生成器;

namespace 変成将棋.ViewModels;

public class C対局VM : INotifyPropertyChanged
{
    private static readonly E駒種[] 持ち駒順序 =
        [E駒種.飛車, E駒種.角行, E駒種.金将, E駒種.銀将, E駒種.桂馬, E駒種.香車, E駒種.歩兵];

    private readonly C盤面 _盤面 = new();
    private readonly Dictionary<byte, C升VM> _升VMMap = [];

    // 通常選択状態
    private byte? _選択中Byte;
    private E駒種? _選択中持ち駒;
    private E手番? _選択中持ち駒手番;
    private readonly Dictionary<byte, List<S手>> _移動先一覧 = [];

    // 獅王 2回移動：中間モード
    private byte? _中間升Byte;
    private List<S手>? _1歩手;
    private Dictionary<byte, List<S手>>? _保存済み移動先;

    private static readonly IプレイヤーAI _ランダムAI = new CランダムAI();

    private bool _ゲームオーバー;
    private bool _先手CPU;
    private bool _後手CPU;
    private bool _先手AI;
    private bool _後手AI;
    private IプレイヤーAI? _AIプレイヤー;

    // 棋譜
    private readonly List<string> _棋譜SFENs = [];
    private int _再生位置 = -1;   // -1 = 対局中

    // 最終手ハイライト
    private readonly List<byte> _最終手升 = [];

    // 思考時間
    private TimeSpan _先手合計 = TimeSpan.Zero;
    private TimeSpan _後手合計 = TimeSpan.Zero;
    private DateTime _手番開始 = DateTime.Now;
    private bool     _対局開始済み = false;
    private readonly DispatcherTimer _タイマー;
    private DispatcherTimer? _自動対局タイマー;
    private CancellationTokenSource? _自動対局CTS;

    public ObservableCollection<C升VM>      升一覧         { get; } = [];
    public ObservableCollection<C持ち駒VM> 先手持ち駒一覧 { get; } = [];
    public ObservableCollection<C持ち駒VM> 後手持ち駒一覧 { get; } = [];

    public IReadOnlyList<string> 列番号一覧 { get; } = ["９","８","７","６","５","４","３","２","１"];
    public IReadOnlyList<string> 段番号一覧 { get; } = ["一","二","三","四","五","六","七","八","九"];

    public Func<bool>? 成り選択要求 { get; set; }

    public string 手番表示 => _ゲームオーバー ? "" : (_盤面.手番 == E手番.先手 ? "▲ 先手番" : "△ 後手番");

    public bool ゲームオーバー
    {
        get => _ゲームオーバー;
        private set
        {
            _ゲームオーバー = value;
            OnPropertyChanged(nameof(ゲームオーバー));
            OnPropertyChanged(nameof(手番表示));
            OnPropertyChanged(nameof(勝者表示));
        }
    }

    // 棋譜再生
    public bool 棋譜再生中  => _再生位置 >= 0;
    public bool 前の手可能  => _再生位置 > 0;
    public bool 次の手可能  => _再生位置 >= 0 && _再生位置 < _棋譜SFENs.Count - 1;
    public string 手数表示  => 棋譜再生中
        ? $"{_再生位置} / {_棋譜SFENs.Count - 1} 手"
        : $"{_棋譜SFENs.Count - 1} 手";
    public IReadOnlyList<string> 棋譜SFENs => _棋譜SFENs;

    public string 勝者表示
        => _ゲームオーバー
            ? (_盤面.手番 == E手番.先手 ? "後手の勝ち" : "先手の勝ち")
            : "";

    // 対局者モード（メニューの IsChecked バインディング用）
    public bool Mode先後人間    => !_先手CPU && !_後手CPU && !_先手AI && !_後手AI;
    public bool Mode先人間後CPU => !_先手CPU &&  _後手CPU && !_先手AI && !_後手AI;
    public bool Mode先CPU後人間 =>  _先手CPU && !_後手CPU && !_先手AI && !_後手AI;
    public bool Mode先後CPU     =>  _先手CPU &&  _後手CPU && !_先手AI && !_後手AI;
    public bool Mode先人間後AI  => !_先手CPU && !_後手CPU && !_先手AI &&  _後手AI;
    public bool Mode先AI後人間  => !_先手CPU && !_後手CPU &&  _先手AI && !_後手AI;
    public bool Mode先AI後CPU   =>  _先手AI  &&  _後手CPU && !_先手CPU && !_後手AI;
    public bool Mode先CPU後AI   =>  _先手CPU &&  _後手AI  && !_先手AI  && !_後手CPU;
    public bool AIモデル読み込み済み => _AIプレイヤー is not null;

    // 思考時間表示
    public string 先手時間表示
    {
        get
        {
            var 今手 = _対局開始済み && !_ゲームオーバー && _盤面.手番 == E手番.先手
                ? DateTime.Now - _手番開始 : TimeSpan.Zero;
            var suffix = 今手 > TimeSpan.Zero ? $"  今手 {今手.TotalSeconds:F1}秒" : "";
            return $"▲先手  合計 {FormatTime(_先手合計 + 今手)}{suffix}";
        }
    }
    public string 後手時間表示
    {
        get
        {
            var 今手 = _対局開始済み && !_ゲームオーバー && _盤面.手番 == E手番.後手
                ? DateTime.Now - _手番開始 : TimeSpan.Zero;
            var suffix = 今手 > TimeSpan.Zero ? $"  今手 {今手.TotalSeconds:F1}秒" : "";
            return $"△後手  合計 {FormatTime(_後手合計 + 今手)}{suffix}";
        }
    }
    private static string FormatTime(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:D2}";

    // メニューコマンド
    public ICommand 盤面初期化コマンド  { get; }
    public ICommand 先後人間コマンド    { get; }
    public ICommand 先人間後CPUコマンド { get; }
    public ICommand 先CPU後人間コマンド { get; }
    public ICommand 先後CPUコマンド    { get; }
    public ICommand 先人間後AIコマンド  { get; }
    public ICommand 先AI後人間コマンド  { get; }
    public ICommand 先AI後CPUコマンド   { get; }
    public ICommand 先CPU後AIコマンド   { get; }

    // 棋譜ナビゲーションコマンド
    public ICommand 最初の手コマンド    { get; }
    public ICommand 前の手コマンド      { get; }
    public ICommand 次の手コマンド      { get; }
    public ICommand 最後の手コマンド    { get; }
    public ICommand 棋譜再生終了コマンド { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public C対局VM()
    {
        for (int 段 = 1; 段 <= 9; 段++)
        {
            for (int 列 = 9; 列 >= 1; 列--)
            {
                var 升 = _盤面.Get升(列, 段);
                var vm = new C升VM(升, OnSquareClicked);
                升一覧.Add(vm);
                _升VMMap[vm.Byte値] = vm;
            }
        }
        foreach (var 種類 in 持ち駒順序)
        {
            先手持ち駒一覧.Add(new C持ち駒VM(種類, E手番.先手, OnMochigomaClicked));
            後手持ち駒一覧.Add(new C持ち駒VM(種類, E手番.後手, OnMochigomaClicked));
        }

        盤面初期化コマンド   = new CRelayCommand(盤面初期化);
        先後人間コマンド     = new CRelayCommand(() => Set対局者(先手CPU: false, 後手CPU: false));
        先人間後CPUコマンド  = new CRelayCommand(() => Set対局者(先手CPU: false, 後手CPU: true));
        先CPU後人間コマンド  = new CRelayCommand(() => Set対局者(先手CPU: true,  後手CPU: false));
        先後CPUコマンド      = new CRelayCommand(() => Set対局者(先手CPU: true,  後手CPU: true));
        先人間後AIコマンド   = new CRelayCommand(() => SetAI対局者(先手AI: false, 後手AI: true));
        先AI後人間コマンド   = new CRelayCommand(() => SetAI対局者(先手AI: true,  後手AI: false));
        先AI後CPUコマンド    = new CRelayCommand(() => SetAI対CPU対局者(先手AI: true));
        先CPU後AIコマンド    = new CRelayCommand(() => SetAI対CPU対局者(先手AI: false));
        最初の手コマンド     = new CRelayCommand(() => 棋譜位置へ移動(0));
        前の手コマンド       = new CRelayCommand(() => 棋譜位置へ移動(_再生位置 - 1));
        次の手コマンド       = new CRelayCommand(() => 棋譜位置へ移動(_再生位置 + 1));
        最後の手コマンド     = new CRelayCommand(() => 棋譜位置へ移動(_棋譜SFENs.Count - 1));
        棋譜再生終了コマンド = new CRelayCommand(棋譜再生を終了);

        // 初期局面を棋譜の0手目として記録
        _棋譜SFENs.Add(_盤面.ToSFEN());

        _タイマー = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _タイマー.Tick += (_, _) =>
        {
            if (_ゲームオーバー) return;
            // 番手中のプレイヤーの「今手」をライブ更新
            if (_盤面.手番 == E手番.先手) OnPropertyChanged(nameof(先手時間表示));
            else                          OnPropertyChanged(nameof(後手時間表示));
        };
        _タイマー.Start();
    }

    // ===== メニュー処理 =====

    private void 盤面初期化()
    {
        _自動対局タイマー?.Stop();
        Deselect();
        _盤面.Reset();
        foreach (var vm in 升一覧) vm.Refresh();
        Update持ち駒枚数();
        OnPropertyChanged(nameof(手番表示));
        foreach (var b in _最終手升)
            if (_升VMMap.TryGetValue(b, out var vm)) vm.Is最終手 = false;
        _最終手升.Clear();
        _先手合計 = TimeSpan.Zero;
        _後手合計 = TimeSpan.Zero;
        _手番開始 = DateTime.Now;
        _対局開始済み = false;
        OnPropertyChanged(nameof(先手時間表示));
        OnPropertyChanged(nameof(後手時間表示));
        ゲームオーバー = false;
        _棋譜SFENs.Clear();
        _棋譜SFENs.Add(_盤面.ToSFEN());
        _再生位置 = -1;
        Notify棋譜Changed();
        Try自動();
    }

    private void Set対局者(bool 先手CPU, bool 後手CPU)
    {
        _先手CPU = 先手CPU; _後手CPU = 後手CPU;
        _先手AI  = false;   _後手AI  = false;
        NotifyモードChanged();
        Try自動();
    }

    private void SetAI対局者(bool 先手AI, bool 後手AI)
    {
        if (_AIプレイヤー is null) return;
        _先手AI = 先手AI; _後手AI = 後手AI;
        _先手CPU = false; _後手CPU = false;
        NotifyモードChanged();
        Try自動();
    }

    private void SetAI対CPU対局者(bool 先手AI)
    {
        if (_AIプレイヤー is null) return;
        _先手AI  =  先手AI; _後手AI  = !先手AI;
        _先手CPU = !先手AI; _後手CPU =  先手AI;
        NotifyモードChanged();
        Try自動();
    }

    public void AIモデルを読み込む(string path)
    {
        _AIプレイヤー?.Dispose();
        _AIプレイヤー = new CMCTSPlayer(path);
        OnPropertyChanged(nameof(AIモデル読み込み済み));
        NotifyモードChanged();
    }

    private void NotifyモードChanged()
    {
        OnPropertyChanged(nameof(Mode先後人間));
        OnPropertyChanged(nameof(Mode先人間後CPU));
        OnPropertyChanged(nameof(Mode先CPU後人間));
        OnPropertyChanged(nameof(Mode先後CPU));
        OnPropertyChanged(nameof(Mode先人間後AI));
        OnPropertyChanged(nameof(Mode先AI後人間));
        OnPropertyChanged(nameof(Mode先AI後CPU));
        OnPropertyChanged(nameof(Mode先CPU後AI));
    }

    // ===== 自動実行（CPU / AI） =====

    private bool Is自動番()
        => (_盤面.手番 == E手番.先手 && (_先手CPU || _先手AI)) ||
           (_盤面.手番 == E手番.後手 && (_後手CPU || _後手AI));

    private void Stop自動対局()
    {
        _自動対局タイマー?.Stop();
        _自動対局CTS?.Cancel();
        _自動対局CTS = null;
    }

    private void Try自動()
    {
        if (棋譜再生中) return;

        if (_先手CPU && _後手CPU)
        {
            Stop自動対局();
            if (_ゲームオーバー) return;
            _自動対局タイマー = new DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(150) };
            _自動対局タイマー.Tick += (_, _) => CPU対CPUステップ();
            _自動対局タイマー.Start();
            return;
        }

        if ((_先手AI && _後手CPU) || (_先手CPU && _後手AI))
        {
            Stop自動対局();
            if (!_ゲームオーバー) _ = AI対CPU対局Async();
            return;
        }

        Stop自動対局();
        while (!_ゲームオーバー && Is自動番())
        {
            bool useAI = _盤面.手番 == E手番.先手 ? _先手AI : _後手AI;
            var 手 = (useAI && _AIプレイヤー is not null)
                ? _AIプレイヤー.Get手(_盤面)
                : _ランダムAI.Get手(_盤面);
            if (手 == null) { ゲームオーバー = true; break; }
            Apply手(手.Value);
        }
    }

    private async Task AI対CPU対局Async()
    {
        var cts = new CancellationTokenSource();
        _自動対局CTS = cts;
        try
        {
            while (!_ゲームオーバー && Is自動番() && !cts.Token.IsCancellationRequested)
            {
                bool useAI = _盤面.手番 == E手番.先手 ? _先手AI : _後手AI;
                S手? 手;
                if (useAI && _AIプレイヤー is not null)
                {
                    var player = _AIプレイヤー;
                    var board  = _盤面;
                    手 = await Task.Run(() => player.Get手(board), cts.Token);
                }
                else
                {
                    手 = _ランダムAI.Get手(_盤面);
                    await Task.Delay(150, cts.Token);
                }
                if (cts.Token.IsCancellationRequested) break;
                if (手 == null) { ゲームオーバー = true; break; }
                Apply手(手.Value);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_自動対局CTS == cts) _自動対局CTS = null;
        }
    }

    private void CPU対CPUステップ()
    {
        if (_ゲームオーバー || !Is自動番())
        {
            _自動対局タイマー?.Stop();
            return;
        }
        var 手 = _ランダムAI.Get手(_盤面);
        if (手 == null) { ゲームオーバー = true; _自動対局タイマー?.Stop(); return; }
        Apply手(手.Value);
    }

    // ===== クリック処理 =====

    private void OnSquareClicked(byte 升Byte)
    {
        if (_ゲームオーバー || Is自動番() || 棋譜再生中) return;

        var 升 = new S升座標(升Byte);
        var 駒 = _盤面.Get駒(升);

        // --- 持ち駒選択中 ---
        if (_選択中持ち駒 != null)
        {
            if (_移動先一覧.TryGetValue(升Byte, out var 打ち手))
            {
                Execute手(打ち手);
            }
            else
            {
                Deselect();
                if (駒?.手番 == _盤面.手番) Select(升Byte);
            }
            return;
        }

        // --- 獅王 2回移動：中間モード ---
        if (_中間升Byte != null)
        {
            if (升Byte == _中間升Byte)
            {
                // 中間升を再クリック → 1歩で止まる
                if (_1歩手?.Count > 0)
                    Execute手(_1歩手);
                else
                    Deselect();
            }
            else if (_移動先一覧.TryGetValue(升Byte, out var 二回手))
            {
                Execute手(二回手);
            }
            else
            {
                // 無効な升 → 中間モード解除、元の選択状態に戻す
                Exit中間モード();
                if (駒?.手番 == _盤面.手番 && 升Byte != _選択中Byte)
                {
                    Deselect();
                    Select(升Byte);
                }
            }
            return;
        }

        // --- 通常モード ---
        if (_選択中Byte == null)
        {
            if (駒?.手番 == _盤面.手番) Select(升Byte);
        }
        else if (升Byte == _選択中Byte)
        {
            Deselect();
        }
        else if (_移動先一覧.TryGetValue(升Byte, out var 手リスト))
        {
            // 獅王で隣接升クリック → 中間モードへ（2回移動の1手目）
            if (Is獅王選択中() && チェビシェフ距離(_選択中Byte.Value, 升Byte) == 1)
                Enter中間モード(升Byte);
            else
                Execute手(手リスト);
        }
        else if (駒?.手番 == _盤面.手番)
        {
            Deselect();
            Select(升Byte);
        }
        else
        {
            Deselect();
        }
    }

    private void OnMochigomaClicked(E手番 手番, E駒種 駒種)
    {
        if (_ゲームオーバー || Is自動番() || 棋譜再生中) return;
        if (手番 != _盤面.手番) return;

        if (_選択中持ち駒 == 駒種 && _選択中持ち駒手番 == 手番)
        {
            Deselect();
            return;
        }

        Deselect();
        _選択中持ち駒 = 駒種;
        _選択中持ち駒手番 = 手番;

        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];
        int 手数 = C合法手生成器.Get合法手(_盤面, バッファ);

        for (int i = 0; i < 手数; i++)
        {
            var 手 = バッファ[i];
            if (!手.Is打ち || 手.Get打ち駒 != 駒種) continue;
            var 先Byte = 手.Get移動先.Byte値;
            if (!_移動先一覧.ContainsKey(先Byte)) _移動先一覧[先Byte] = [];
            _移動先一覧[先Byte].Add(手);
        }

        foreach (var key in _移動先一覧.Keys)
        {
            if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = true;
        }

        var 持ち駒一覧 = 手番 == E手番.先手 ? 先手持ち駒一覧 : 後手持ち駒一覧;
        foreach (var vm in 持ち駒一覧)
        {
            if (vm.種類 == 駒種) vm.Is選択中 = true;
        }
    }

    // ===== 選択・中間モード =====

    private void Select(byte 升Byte)
    {
        _選択中Byte = 升Byte;
        _移動先一覧.Clear();

        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];
        int 手数 = C合法手生成器.Get合法手(_盤面, バッファ);

        for (int i = 0; i < 手数; i++)
        {
            var 手 = バッファ[i];
            if (手.Is打ち || 手.Get移動元.Byte値 != 升Byte) continue;
            var 先Byte = 手.Get移動先.Byte値;
            if (!_移動先一覧.ContainsKey(先Byte)) _移動先一覧[先Byte] = [];
            _移動先一覧[先Byte].Add(手);

            // 獅王2回移動の中間升も登録（被利き升でも通過点として中間モードに入れるよう）
            if (手.Is獅王2回移動)
            {
                var 中間Byte = 手.Get中間.Byte値;
                if (!_移動先一覧.ContainsKey(中間Byte)) _移動先一覧[中間Byte] = [];
            }
        }

        if (_升VMMap.TryGetValue(升Byte, out var 選択VM)) 選択VM.Is選択中 = true;
        foreach (var key in _移動先一覧.Keys)
        {
            if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = true;
        }
    }

    // 獅王の中間モードへ移行：隣接升を中間として2手目の選択肢を表示
    private void Enter中間モード(byte 中間Byte)
    {
        // 現在の移動先ハイライトをクリア
        foreach (var key in _移動先一覧.Keys)
        {
            if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = false;
        }

        // 現在の移動先一覧を保存
        _保存済み移動先 = new Dictionary<byte, List<S手>>(_移動先一覧);

        // この中間升への1歩移動手を保存
        _移動先一覧.TryGetValue(中間Byte, out var all手);
        _1歩手 = all手?.Where(h => !h.Is獅王2回移動).ToList();

        // 中間升を経由する2回移動の2手目候補を収集
        _中間升Byte = 中間Byte;
        _移動先一覧.Clear();

        foreach (var (_, 手リスト) in _保存済み移動先)
        {
            foreach (var 手 in 手リスト)
            {
                if (!手.Is獅王2回移動 || 手.Get中間.Byte値 != 中間Byte) continue;
                var 先Byte = 手.Get移動先.Byte値;
                if (!_移動先一覧.ContainsKey(先Byte)) _移動先一覧[先Byte] = [];
                _移動先一覧[先Byte].Add(手);
            }
        }

        // 2手目候補をハイライト
        foreach (var key in _移動先一覧.Keys)
        {
            if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = true;
        }

        // 1歩で止まれる場合、中間升自体もハイライト（再クリックで1歩移動）
        if (_1歩手?.Count > 0)
        {
            _移動先一覧[中間Byte] = _1歩手;
            if (_升VMMap.TryGetValue(中間Byte, out var 中間vm)) 中間vm.Is移動候補 = true;
        }
    }

    // 中間モード解除：元の通常選択状態に戻す
    private void Exit中間モード()
    {
        foreach (var key in _移動先一覧.Keys)
        {
            if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = false;
        }

        _移動先一覧.Clear();
        if (_保存済み移動先 != null)
        {
            foreach (var (k, v) in _保存済み移動先) _移動先一覧[k] = v;
            foreach (var key in _移動先一覧.Keys)
            {
                if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = true;
            }
        }

        _中間升Byte = null;
        _1歩手 = null;
        _保存済み移動先 = null;
    }

    private void Deselect()
    {
        if (_中間升Byte != null)
        {
            foreach (var key in _移動先一覧.Keys)
            {
                if (_升VMMap.TryGetValue(key, out var vm)) vm.Is移動候補 = false;
            }
            _中間升Byte = null;
            _1歩手 = null;
            _保存済み移動先 = null;
        }

        if (_選択中Byte != null && _升VMMap.TryGetValue(_選択中Byte.Value, out var vm2))
            vm2.Is選択中 = false;

        foreach (var key in _移動先一覧.Keys)
        {
            if (_升VMMap.TryGetValue(key, out var 候補VM)) 候補VM.Is移動候補 = false;
        }

        _選択中Byte = null;

        if (_選択中持ち駒 != null)
        {
            var 持ち駒一覧 = _選択中持ち駒手番 == E手番.先手 ? 先手持ち駒一覧 : 後手持ち駒一覧;
            foreach (var mvm in 持ち駒一覧) mvm.Is選択中 = false;
            _選択中持ち駒 = null;
            _選択中持ち駒手番 = null;
        }

        _移動先一覧.Clear();
    }

    // ===== 手の実行 =====

    private void Execute手(List<S手> 手リスト)
    {
        Deselect();

        bool 成り手あり   = 手リスト.Any(h => h.Is成り);
        bool 不成り手あり = 手リスト.Any(h => !h.Is成り);

        S手 実行手;
        if (成り手あり && 不成り手あり)
        {
            bool 成り = 成り選択要求?.Invoke() ?? true;
            実行手 = 手リスト.First(h => h.Is成り == 成り);
        }
        else
        {
            実行手 = 手リスト.FirstOrDefault(h => !h.Is獅王2回移動, 手リスト[0]);
        }

        Apply手(実行手);
        Try自動();
    }

    private void Apply最終手ハイライト(S手 手)
    {
        foreach (var b in _最終手升)
            if (_升VMMap.TryGetValue(b, out var vm)) vm.Is最終手 = false;
        _最終手升.Clear();

        void Mark(byte b) { _最終手升.Add(b); if (_升VMMap.TryGetValue(b, out var vm)) vm.Is最終手 = true; }
        Mark(手.移動先);
    }

    private void Apply手(S手 手)
    {
        // 手番切り替え前に思考時間を記録（最初の一手からカウント開始）
        if (_対局開始済み)
        {
            var elapsed = DateTime.Now - _手番開始;
            if (_盤面.手番 == E手番.先手) _先手合計 += elapsed;
            else                          _後手合計 += elapsed;
        }
        else
        {
            _対局開始済み = true;
        }
        _手番開始 = DateTime.Now;

        _盤面.Apply(手);
        Apply最終手ハイライト(手);
        foreach (var vm in 升一覧) vm.Refresh();
        Update持ち駒枚数();
        OnPropertyChanged(nameof(手番表示));
        OnPropertyChanged(nameof(先手時間表示));
        OnPropertyChanged(nameof(後手時間表示));

        // 棋譜に記録
        _棋譜SFENs.Add(_盤面.ToSFEN());
        Notify棋譜Changed();

        Span<S手> バッファ = stackalloc S手[C合法手生成器.最大手数];
        if (C合法手生成器.Get合法手(_盤面, バッファ) == 0)
            ゲームオーバー = true;
    }

    private void Update持ち駒枚数()
    {
        foreach (var vm in 先手持ち駒一覧)
            vm.枚数 = _盤面.先手持ち駒.GetValueOrDefault(vm.種類, 0);
        foreach (var vm in 後手持ち駒一覧)
            vm.枚数 = _盤面.後手持ち駒.GetValueOrDefault(vm.種類, 0);
    }

    // ===== 棋譜 =====

    private void 棋譜位置へ移動(int 位置)
    {
        if (_棋譜SFENs.Count == 0) return;
        Deselect();
        foreach (var b in _最終手升)
            if (_升VMMap.TryGetValue(b, out var v)) v.Is最終手 = false;
        _最終手升.Clear();

        _再生位置 = Math.Clamp(位置, 0, _棋譜SFENs.Count - 1);
        _盤面.Reset(_棋譜SFENs[_再生位置]);
        foreach (var vm in 升一覧) vm.Refresh();
        Update持ち駒枚数();
        Notify棋譜Changed();
    }

    private void 棋譜再生を終了()
    {
        _再生位置 = -1;
        // ライブ局面（最後に記録した SFEN）を復元
        _盤面.Reset(_棋譜SFENs[^1]);
        foreach (var vm in 升一覧) vm.Refresh();
        Update持ち駒枚数();
        Notify棋譜Changed();
    }

    public void 棋譜を読み込む(IEnumerable<string> lines)
    {
        var sfens = lines
            .Where(l => !l.StartsWith('#') && !string.IsNullOrWhiteSpace(l))
            .ToList();
        if (sfens.Count == 0) return;

        _棋譜SFENs.Clear();
        _棋譜SFENs.AddRange(sfens);
        ゲームオーバー = false;
        棋譜位置へ移動(_棋譜SFENs.Count - 1);
    }

    private void Notify棋譜Changed()
    {
        OnPropertyChanged(nameof(棋譜再生中));
        OnPropertyChanged(nameof(前の手可能));
        OnPropertyChanged(nameof(次の手可能));
        OnPropertyChanged(nameof(手数表示));
        OnPropertyChanged(nameof(手番表示));
    }

    // ===== ヘルパー =====

    private bool Is獅王選択中()
    {
        if (_選択中Byte == null) return false;
        return _盤面.Get駒(new S升座標(_選択中Byte.Value))?.種類 == E駒種.獅王;
    }

    private static int チェビシェフ距離(byte a, byte b)
    {
        var 升a = new S升座標(a);
        var 升b = new S升座標(b);
        return Math.Max(Math.Abs(升a.列 - 升b.列), Math.Abs(升a.段 - 升b.段));
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
