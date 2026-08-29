using BentaGo.App.Mvvm;

namespace BentaGo.App.Navigation;

/// <summary>
/// Raised after the active screen changes (bg-004-synthesis §5.1). Carries
/// the activated view model so the shell can bind it into the ContentControl.
/// </summary>
public sealed record NavigationEventArgs(
    string FromKey,
    string ToKey,
    string? FocusElement,
    ViewModelBase ViewModel);
