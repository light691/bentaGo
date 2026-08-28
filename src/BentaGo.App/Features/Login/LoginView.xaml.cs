using System.Windows.Controls;
using System.Windows.Input;

namespace BentaGo.App.Features.Login;

public partial class LoginView : UserControl
{
    private LoginViewModel? _viewModel;

    public LoginView()
    {
        InitializeComponent();
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                vm.Password = PasswordBox.Password;
            }
        };
        DataContextChanged += (_, _) => _viewModel = DataContext as LoginViewModel;
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel is { CanLogin: true } vm && vm.LoginCommand.CanExecute(null))
        {
            vm.LoginCommand.Execute(null);
            e.Handled = true;
        }
    }
}
