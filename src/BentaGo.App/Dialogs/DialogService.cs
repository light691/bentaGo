using System.Windows;
using System.Windows.Input;
using BentaGo.App.Navigation;

namespace BentaGo.App.Dialogs;

/// <summary>
/// The only type that calls ShowDialog (bg-004-synthesis §7.2): hosts the
/// dialog view model in an owner-modal DialogHost window, binds the view
/// model's CloseRequested to window close, awaits the modal loop, restores
/// focus symbolically, and disposes the dialog view model once on close.
/// A second ShowAsync from a non-top scope is guarded by <see cref="IsOpen"/>.
/// </summary>
public sealed class DialogService : IDialogService
{
    private readonly IFocusService _focus;

    public DialogService(IFocusService focus) => _focus = focus;

    public bool IsOpen { get; private set; }

    public Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (IsOpen)
        {
            throw new InvalidOperationException(
                "A modal dialog is already open; dialogs may only be opened from the top scope.");
        }

        IsOpen = true;
        TResult? result = null;
        try
        {
            // Capture the currently focused element symbolically before the
            // modal loop takes input scope (bg-004-synthesis §7.2).
            string? focusedName = FocusCoordinator.CurrentFocusedElementName();

            var window = new DialogHost { DataContext = viewModel };
            if (System.Windows.Application.Current?.MainWindow is { } owner)
            {
                window.Owner = owner;
            }

            viewModel.CloseRequested += (_, e) =>
            {
                result = e.Result;
                window.Close();
            };

            // Modal nested loop: the UI thread keeps pumping while the
            // dialog is open; Esc / Enter / F6 bindings live in DialogHost.
            window.ShowDialog();

            string? restore = focusAfterClose ?? focusedName;
            if (restore is not null)
            {
                _focus.Request(restore);
            }

            return Task.FromResult(result);
        }
        finally
        {
            IsOpen = false;
            viewModel.Dispose();
        }
    }
}