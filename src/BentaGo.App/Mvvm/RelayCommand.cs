using System.Windows.Input;

namespace BentaGo.App.Mvvm;

/// <summary>
/// Minimal ICommand implementation backed by an Action and an optional
/// CanExecute predicate. BG-004 adds <see cref="NotifyCanExecuteChanged"/>
/// (bg-004-synthesis §10, C-graft): view models call it from setters that
/// affect enablement so CanExecute tests are deterministic instead of
/// depending on CommandManager requery timing. The RequerySuggested hook is
/// kept for UI scenarios.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;
    private EventHandler? _canExecuteChanged;

    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>Raises CanExecuteChanged directly (deterministic enablement).</summary>
    public void NotifyCanExecuteChanged() => _canExecuteChanged?.Invoke(this, EventArgs.Empty);
}