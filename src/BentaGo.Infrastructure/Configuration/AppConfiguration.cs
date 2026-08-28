using System.IO;
using Microsoft.Extensions.Configuration;

namespace BentaGo.Infrastructure.Configuration;

/// <summary>
/// Loads appsettings.json from the application base directory when present.
/// Absence of the file is not an error: configuration simply stays empty.
/// </summary>
public static class AppConfiguration
{
    public static IConfiguration Load()
    {
        string baseDir = AppContext.BaseDirectory;
        string path = Path.Combine(baseDir, "appsettings.json");

        var builder = new ConfigurationBuilder();

        if (File.Exists(path))
        {
            builder.AddJsonFile(path, optional: false, reloadOnChange: false);
        }

        return builder.Build();
    }
}