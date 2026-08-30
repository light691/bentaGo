using System.Windows.Controls;

namespace BentaGo.App.Dialogs.Corrections;

/// <summary>
/// Corrections panel host (bg-004-synthesis §7 pattern): markup + bindings
/// only; the state and rules live in <see cref="CorrectionsDialogViewModel"/>.
/// </summary>
public partial class CorrectionsDialogView : UserControl
{
    public CorrectionsDialogView()
    {
        InitializeComponent();
    }
}
