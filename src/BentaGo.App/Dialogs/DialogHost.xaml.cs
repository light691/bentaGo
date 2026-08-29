using System.Windows;

namespace BentaGo.App.Dialogs;

/// <summary>
/// Generic modal host window (bg-004-synthesis §7.1): hosts the dialog view
/// model through the implicit data template; the DialogService owns the
/// lifecycle. No code-behind logic - the window is pure chrome.
/// </summary>
public partial class DialogHost : Window
{
    public DialogHost() => InitializeComponent();
}
