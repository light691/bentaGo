using System.Windows.Input;
using BentaGo.App.Mvvm;

namespace BentaGo.App.Dialogs.Confirm;

/// <summary>
/// Simple confirmation dialog (bg-004-synthesis §7.1): Confirm closes with
/// true; Esc/Cancel dismisses to null, which the caller treats as false.
/// </summary>
public sealed class ConfirmDialogViewModel : DialogViewModelBase<ConfirmDialogResult>
{
    private string _message = "";

    public ConfirmDialogViewModel()
    {
        ConfirmCommand = new RelayCommand(_ => Close(new ConfirmDialogResult(true)));
    }

    public ICommand ConfirmCommand { get; }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }
}

public sealed record ConfirmDialogResult(bool Confirmed);