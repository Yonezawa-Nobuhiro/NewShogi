using System.Windows.Input;

namespace 変成将棋.ViewModels;

public class CRelayCommand : ICommand
{
    private readonly Action _execute;

    public CRelayCommand(Action execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();

    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
