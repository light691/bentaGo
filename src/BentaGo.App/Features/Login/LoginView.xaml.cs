using System.Windows.Controls;

namespace BentaGo.App.Features.Login;

/// <summary>
/// Login code-behind contains ONLY the PasswordBox -&gt; view model bridge
/// (Password has no bindable dependency property). Initial focus arrives
/// through the shell's FocusCoordinator (FocusRequest "Login.UsernameBox"),
/// and Enter submits through the Log in button's IsDefault - there is no
/// Enter handler and no focus code here (bg-004-synthesis §16, open
/// question 4).
/// </summary>
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
    }
}