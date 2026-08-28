using BentaGo.App.Features.Login;
using BentaGo.Infrastructure.Hosting;
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

        _provider = BentaGoHost.Build(extra: services =>
        {
            services.AddTransient<LoginViewModel>();
            services.AddTransient<MainViewModel>();
        });
        return _provider;
    }
}
