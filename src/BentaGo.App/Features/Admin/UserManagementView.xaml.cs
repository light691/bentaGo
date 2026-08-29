using System.Windows.Controls;

namespace BentaGo.App.Features.Admin;

/// <summary>
/// User management view: code-behind carries ONLY the PasswordBox -&gt; view
/// model bridges (Password has no bindable dependency property — the
/// LoginView precedent). Initial focus arrives through the shell's
/// FocusCoordinator ("Admin.Users.Username"); Enter/F5/F6/F7 live in the
/// input bindings declared in the markup.
/// </summary>
public partial class UserManagementView : UserControl
{
    private UserManagementViewModel? _viewModel;

    public UserManagementView()
    {
        InitializeComponent();
        NewPasswordBox.PasswordChanged += (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                vm.NewPassword = NewPasswordBox.Password;
            }
        };
        ChangePasswordBox.PasswordChanged += (_, _) =>
        {
            if (_viewModel is { } vm)
            {
                vm.ChangePasswordNew = ChangePasswordBox.Password;
            }
        };
        DataContextChanged += (_, _) => _viewModel = DataContext as UserManagementViewModel;
    }
}
