using Microsoft.Extensions.DependencyInjection;

namespace BentaGo.App.Mvvm;

/// <summary>
/// Builds the application service provider: BentaGoHost services plus
/// App-level view model registrations.
/// </summary>
public static class ObservableObjectFactory
{
    private static ServiceProvider? _provider;

    public static IServiceProvider Build()
    {
        if (_provider is not null)
        {
            return _provider;
        }

        var services = new ServiceCollection();

        // BentaGoHost registrations (DI + logging + data).
        Infrastructure.BentaGoHost.Build(services);

        // App-level view models.
        services.AddTransient<Features.Login.LoginViewModel>();
        services.AddTransient<MainViewModel>();

        _provider = services.BuildServiceProvider();
        return _provider;
    }
}