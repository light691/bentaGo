namespace BentaGo.Infrastructure.Backup;

public sealed record BackupReceipt(
    string BackupPath,
    string DatabaseVersion,
    string Sha256,
    DateTime CreatedUtc);
