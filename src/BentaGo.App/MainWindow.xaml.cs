using System.Windows;
using System.Windows.Input;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation.Shortcuts;

namespace BentaGo.App;

/// <summary>
/// The BG-001 shell extended (bg-004-synthesis §5.2). Code-behind is limited
/// to WPF-forced mechanisms: the window-level KeyBindings generated from
/// ShortcutCatalog (gesture list, parameters only - no logic) and the
/// constructor wiring. Focus resolution lives in the FocusCoordinator.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        AddWindowBindings();
    }

    /// <summary>
    /// One InputBinding per window-bound Active shortcut, generated from the
    /// catalog so binding/catalog parity holds by construction (§6.3.8).
    /// </summary>
    private void AddWindowBindings()
    {
        foreach (ShortcutEntry entry in ShortcutCatalog.WindowBindings)
        {
            (ICommand command, object? parameter) = _viewModel.CommandMapping(entry.Id);
            InputBindings.Add(new KeyBinding(command, entry.Key, entry.Modifiers)
            {
                CommandParameter = parameter,
            });
        }
    }
}