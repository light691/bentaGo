namespace BentaGo.Infrastructure.Backup;

public sealed record RestoreReceipt(
    string BackupPath,
    string RestoredPath,
    string PreRestoreCopyPath,
    bool Verified);