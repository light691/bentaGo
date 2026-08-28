using System.IO;

namespace BentaGo.Infrastructure.Configuration;

public interface IDataPathProvider
{
    string Root { get; }
    string DataDir { get; }
    string BackupsDir { get; }
    string LogsDir { get; }
    void EnsureCreated();
}

public sealed class DataPathProvider : IDataPathProvider
{
    public string Root { get; }
    public string DataDir { get; }
    public string BackupsDir { get; }
    public string LogsDir { get; }

    public DataPathProvider(string? rootOverride = null)
    {
        Root = rootOverride
            ?? Environment.GetEnvironmentVariable("BENTAGO_DATA_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BentaGo");
        DataDir = Path.Combine(Root, "data");
        BackupsDir = Path.Combine(Root, "backups");
        LogsDir = Path.Combine(Root, "logs");
    }

    public void EnsureCreated()
    {
        foreach (string dir in new[] { Root, DataDir, BackupsDir, LogsDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    $"BentaGo data directory could not be created at '{dir}'. " +
                    "Check permissions, or set the BENTAGO_DATA_ROOT environment variable to a writable location.", ex);
            }
        }
    }
}
