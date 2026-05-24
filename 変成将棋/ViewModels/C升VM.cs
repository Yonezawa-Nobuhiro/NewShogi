using System.ComponentModel;
using System.Windows.Input;
using 変成将棋.Models;

namespace 変成将棋.ViewModels;

public class C升VM : INotifyPropertyChanged
{
    private readonly C升 _升;
    private bool _是選択中;
    private bool _是移動候補;

    public string 表示文字 => _升.駒.Is有効 ? _升.駒.Get表示文字() : "";
    public bool 駒あり    => _升.駒.Is有効;
    public bool 先手駒    => _升.駒.Is有効 && _升.駒.手番 == E手番.先手;
    public double 回転角度 => 駒あり && !先手駒 ? 180.0 : 0.0;
    public byte Byte値    => new S升座標((byte)_升.列, (byte)_升.段).Byte値;

    public bool Is選択中
    {
        get => _是選択中;
        set { _是選択中 = value; OnPropertyChanged(nameof(Is選択中)); }
    }

    public bool Is移動候補
    {
        get => _是移動候補;
        set { _是移動候補 = value; OnPropertyChanged(nameof(Is移動候補)); }
    }

    private bool _是最終手;
    public bool Is最終手
    {
        get => _是最終手;
        set { _是最終手 = value; OnPropertyChanged(nameof(Is最終手)); }
    }

    public ICommand クリック { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public C升VM(C升 升, Action<byte> クリックコールバック)
    {
        _升 = 升;
        クリック = new CRelayCommand(() => クリックコールバック(Byte値));
    }

    // 盤面変更後に全プロパティを再通知する
    public void Refresh()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
