using System.ComponentModel;
using System.Windows.Input;
using 変成将棋.Models;

namespace 変成将棋.ViewModels;

public class C持ち駒VM : INotifyPropertyChanged
{
    private int _枚数;
    private bool _是選択中;

    public E駒種 種類 { get; }
    public E手番 手番 { get; }

    public int 枚数
    {
        get => _枚数;
        set
        {
            _枚数 = value;
            OnPropertyChanged(nameof(枚数));
            OnPropertyChanged(nameof(表示));
            OnPropertyChanged(nameof(駒あり));
        }
    }

    public bool 駒あり => _枚数 > 0;

    // 枚数が1なら文字のみ、2以上なら「文字+枚数」
    public string 表示 => _枚数 > 1
        ? $"{new C駒(種類, 手番).Get表示文字()}{_枚数}"
        : new C駒(種類, 手番).Get表示文字();

    public bool Is選択中
    {
        get => _是選択中;
        set { _是選択中 = value; OnPropertyChanged(nameof(Is選択中)); }
    }

    public ICommand クリック { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public C持ち駒VM(E駒種 種類, E手番 手番, Action<E手番, E駒種> クリックコールバック)
    {
        this.種類 = 種類;
        this.手番 = 手番;
        クリック = new CRelayCommand(() => クリックコールバック(手番, 種類));
    }

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
