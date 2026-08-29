using System.Windows.Input;
using BentaGo.App.Mvvm;

namespace BentaGo.App.Dialogs;

/// <summary>
/// Typed dialog base (bg-004-synthesis §7.1, B-graft): the dialog view model
/// authors the result through Close/Cancel; the service observes
/// CloseRequested to close the window. The dialog VM is disposed by the
/// service once on close. Esc ⇔ dismissed ⇔ null result.
/// </summary>
public abstract class DialogViewModelBase<TResult> : ViewModelBase, IDisposable where TResult : class
{
    protected DialogViewModelBase()
    {
        CancelCommand = new RelayCommand(_ => Cancel());
    }

    /// <summary>Raised with a non-null result to confirm, null to dismiss.</summary>
    public event EventHandler<DialogResultRequest<TResult>>? CloseRequested;

    /// <summary>Every dialog closes on Esc (dialog-window-level binding).</summary>
    public ICommand CancelCommand { get; }

    protected void Close(TResult result) => CloseRequested?.Invoke(this, new DialogResultRequest<TResult>(result));

    protected void Cancel() => CloseRequested?.Invoke(this, new DialogResultRequest<TResult>(null));

    public virtual void Dispose()
    {
    }
}
