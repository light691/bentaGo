using System.IO;
using BentaGo.App;
using BentaGo.App.Features.Login;
using BentaGo.Infrastructure.Hosting;
using BentaGo.Infrastructure.Persistence;
using BentaGo.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Foundation;

/// <summary>
/// Placeholder-shell login behavior against the seeded admin key/value rows.
/// Real user and role tests arrive with BG-005.
/// </summary>
public sealed class LoginViewModelTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "bentago-login-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider? _host;

    private async Task<ServiceProvider> CreateSeededHostAsync()
    {
        var host = BentaGoHost.BuildForTests(_tempRoot);
        var initializer = host.GetRequiredService<DbInitializer>();
        await initializer.EnsureCreatedOrMigrateAsync();
        await host.GetRequiredService<IAdminSeeder>().SeedAsync();
        _host = host;
        return host;
    }

    [Fact]
    public async Task EmptyFieldsDisableLoginCommand()
    {
        var host = await CreateSeededHostAsync();
        var vm = new LoginViewModel(host);
        Assert.False(vm.CanLogin);
        Assert.False(vm.LoginCommand.CanExecute(null));

        vm.Username = "admin";
        Assert.False(vm.CanLogin);

        vm.Password = "admin";
        Assert.True(vm.CanLogin);
        Assert.True(vm.LoginCommand.CanExecute(null));
    }

    [Fact]
    public async Task InvalidCredentialsSetGenericErrorWithoutAuthenticating()
    {
        var host = await CreateSeededHostAsync();
        var vm = new LoginViewModel(host);
        var authenticated = false;
        vm.Authenticated += (_, _) => authenticated = true;

        vm.Username = "admin";
        vm.Password = "wrong-password";
        vm.LoginCommand.Execute(null);
        await Task.Delay(200);

        Assert.False(authenticated);
        Assert.Equal("Sign in failed. Check your username and password.", vm.ErrorMessage);
        Assert.DoesNotContain("wrong-password", vm.ErrorMessage);
    }

    [Fact]
    public async Task ValidCredentialsAuthenticateAndMainViewReadies()
    {
        var host = await CreateSeededHostAsync();
        var main = new MainViewModel(host);
        var loggedInUser = "";
        main.Login.Authenticated += (_, username) => loggedInUser = username;

        main.Login.Username = "admin";
        main.Login.Password = "admin";
        main.Login.LoginCommand.Execute(null);
        await Task.Delay(200);

        Assert.Equal("admin", loggedInUser);
        Assert.True(main.IsAuthenticated);
        Assert.Equal("Ready", main.StatusText);
    }

    [Fact]
    public async Task PasswordNeverAppearsInErrorMessage()
    {
        var host = await CreateSeededHostAsync();
        var vm = new LoginViewModel(host);

        vm.Username = "admin";
        vm.Password = "super-secret-password";
        vm.LoginCommand.Execute(null);
        await Task.Delay(200);

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("super-secret-password", vm.ErrorMessage);
    }

    public void Dispose()
    {
        _host?.Dispose();
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of the throwaway root.
        }
    }
}
