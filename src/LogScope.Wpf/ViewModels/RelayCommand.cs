using System.Windows.Input;

namespace LogScope.Wpf.ViewModels;

/// <summary>
/// 同期/非同期アクション用のシンプルなコマンド。非同期は例外をメッセージに反映する。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action? _execute;
    private readonly Action<object?>? _executeWithParameter;
    private readonly Func<Task>? _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly Predicate<object?>? _canExecuteWithParameter;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public RelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null)
    {
        _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
        _canExecute = canExecute;
    }

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _executeWithParameter = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecuteWithParameter = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
        => _canExecuteWithParameter?.Invoke(parameter) ?? _canExecute?.Invoke() ?? true;

    public async void Execute(object? parameter)
    {
        try
        {
            if (_executeWithParameter is not null)
            {
                _executeWithParameter(parameter);
            }
            else if (_executeAsync is not null)
            {
                await _executeAsync();
            }
            else
            {
                _execute!();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "LogScope", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
