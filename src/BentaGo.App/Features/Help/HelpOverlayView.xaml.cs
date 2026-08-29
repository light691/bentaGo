using System.Windows.Controls;

namespace BentaGo.App.Features.Help;

/// <summary>
/// F1 help overlay (bg-004-synthesis §6.1): renders ShortcutCatalog verbatim
/// through the overlay view model. Pure markup - no code-behind logic.
/// </summary>
public partial class HelpOverlayView : UserControl
{
    public HelpOverlayView() => InitializeComponent();
}