using System.Reflection;
using BentaGo.App.Features.Login;
using BentaGo.App.Mvvm;
using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App;

/// <summary>
/// Top-level shell state: wraps the login flow and exposes the status line.
/// No business logic here; BG-004/005 replace this placeholder with real
/// navigation and security.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private bool _isAuthenticated;
    private string _statusText = "BentaGo";

    public MainViewModel(IServiceProvider services)
    {
        Login = services.GetRequiredService<LoginViewModel>();
        Login.Authenticated += (_, _) =>
        {
            IsAuthenticated = true;
            StatusText = "Ready";
        };
        VersionText = "v" + (Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0");
    }

    public LoginViewModel Login { get; }

    public bool IsAuthenticated
    {
        get => _isAuthenticated;
        private set => SetProperty(ref _isAuthenticated, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string VersionText { get; }
}
