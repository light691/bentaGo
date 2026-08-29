namespace BentaGo.App.Dialogs;

/// <summary>
/// Dialog service contract (bg-004-synthesis §7.1). Feature view models
/// never see a Window; the service hosts the view model in an owner-modal
/// DialogHost window and returns the typed result (or null when dismissed).
/// </summary>
public interface IDialogService
{
    /// <summary>True while a modal dialog is open (window-level Esc scope + lane assertions).</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Returns the typed result, or null when dismissed (Esc / Cancel).
    /// focusAfterClose: symbolic FocusTarget to re-request after close.
    /// </summary>
    Task<TResult?> ShowAsync<TResult>(DialogViewModelBase<TResult> viewModel, string? focusAfterClose = null)
        where TResult : class;
}
