using System.Windows.Input;
using BentaGo.App.Mvvm;
using BentaGo.Application.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Features.Login;

public sealed class LoginViewModel : ViewModelBase
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
            if (SetProperty(ref _username, value))
            {
                OnPropertyChanged(nameof(CanLogin));
            }
        }
    }

    public string Password
    {
        get => _password;
        set
        {
            if (SetProperty(ref _password, value))
            {
                OnPropertyChanged(nameof(CanLogin));
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

    private async Task LoginAsync()
    {
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
