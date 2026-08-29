using System.Windows.Input;
using BentaGo.App.Mvvm;
using BentaGo.Application.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Login;

public sealed class LoginViewModel : ValidatableViewModelBase
{
    private readonly ILoginService _loginService;
    private string _username = "";
    private string _password = "";
    private string? _errorMessage;
    private bool _isBusy;

    public LoginViewModel(IServiceProvider services)
    {
        _loginService = services.GetRequiredService<ILoginService>();
        LoginCommand = new RelayCommand(async _ => await LoginAsync(), _ => CanLogin);
    }

    public event EventHandler<string>? Authenticated;

    public ICommand LoginCommand { get; }

    public string Username
    {
        get => _username;
        set
        {
            if (SetValidatedField(ref _username, value, v => string.IsNullOrWhiteSpace(v) ? "Username is required." : null))
            {
                OnPropertyChanged(nameof(CanLogin));
                (LoginCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetValidatedField(ref _password, value, v => string.IsNullOrEmpty(v) ? "Password is required." : null))
            {
                OnPropertyChanged(nameof(CanLogin));
                (LoginCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanLogin));
            }
        }
    }

    public bool CanLogin => !IsBusy && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Submit-time field validation (bg-004-synthesis §8.1): blank fields get
    /// corrective copy; the auth failure copy stays form-level and generic,
    /// never field-level (never reveals which credential is wrong).
    /// </summary>
    public void ValidateSubmit()
    {
        SetFieldError(nameof(Username), string.IsNullOrWhiteSpace(Username) ? "Username is required." : null);
        SetFieldError(nameof(Password), string.IsNullOrEmpty(Password) ? "Password is required." : null);
    }

    private async Task LoginAsync()
    {
        ValidateSubmit();
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            if (await _loginService.AuthenticateAsync(Username, Password))
            {
                Authenticated?.Invoke(this, Username);
            }
            else
            {
                ErrorMessage = "Sign in failed. Check your username and password.";
            }
        }
        catch
        {
            ErrorMessage = "Sign in failed. Check your username and password.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}