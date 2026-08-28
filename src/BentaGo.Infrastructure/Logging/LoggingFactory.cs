using System.IO;
using BentaGo.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace BentaGo.Infrastructure.Logging;

public static class LoggingFactory
{
    public static void Configure(IServiceCollection services, IDataPathProvider paths, bool development)
    {
        string logPath = Path.Combine(paths.LogsDir, "bentago-.log");

        var config = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .WriteTo.Console();

        if (development)
        {
            config = config.MinimumLevel.Debug();
        }

        Log.Logger = config.CreateLogger();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));
    }
}