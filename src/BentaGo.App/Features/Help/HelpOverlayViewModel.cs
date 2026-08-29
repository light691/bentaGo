using System.Windows.Input;
using BentaGo.App.Data;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Navigation.Shortcuts;
using BentaGo.App.Ui;

namespace BentaGo.App.Features.Help;

/// <summary>
/// F1 help overlay state (bg-004-synthesis §6.1): binds ShortcutCatalog
/// verbatim so the overlay and the conflict tests can never drift. Reserved/
/// Future rows render greyed. Closing is shell policy (MainViewModel), not
/// this view model's business.
/// </summary>
public sealed class HelpOverlayViewModel : ViewModelBase
{
    public HelpOverlayViewModel()
    {
        Rows = ShortcutCatalog.All
            .Select(entry => new HelpRow(entry.Gesture, entry.Action, entry.Status != ShortcutStatus.Active))
            .ToArray();
    }

    public string Title => "Keyboard shortcuts";

    public string Hint => "Esc closes this help.";

    public IReadOnlyList<HelpRow> Rows { get; }

    public sealed record HelpRow(string Gesture, string Action, bool IsGreyed);
}
