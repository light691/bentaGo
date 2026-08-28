namespace BentaGo.Infrastructure.Configuration;

/// <summary>
/// Minimal application settings, loaded optionally from appsettings.json when present.
/// Kept to a plain POCO on purpose; expand as real settings emerge.
/// </summary>
public sealed class AppSettings
{
    public string Environment { get; set; } = "Production";
}
