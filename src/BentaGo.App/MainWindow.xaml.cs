using System.Windows;
using System.Windows.Input;
using BentaGo.App.Mvvm;

namespace BentaGo.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(ObservableObjectFactory.Build());
        DataContext = _viewModel;
        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1)
        {
            MessageBox.Show(
                "Keyboard shortcuts\n\nF1  Help\nEnter  Submit the login form\nTab  Move focus\nEsc  Close dialogs",
                "BentaGo Help",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            e.Handled = true;
        }
    }
}
