using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Printing;

/// <summary>
/// Printer profile switch persisted in AppSettings (BG-007 printer settings).
/// Software-first: the profile is Fake by default; the device pass flips it to
/// UsbEscPos from the settings screen (or a CLI switch).
/// </summary>
public sealed class PrinterSettingsStore(IDbContextFactory<BentaGoDbContext> factory) : IPrinterSettingsStore
{
    private const string Key = "printer.profile";

    public async Task<PrinterProfile> GetProfileAsync(CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        AppSettingKeyValue? row = await context.AppSettings.FindAsync([Key], ct);
        return Enum.TryParse<PrinterProfile>(row?.Value, out PrinterProfile profile)
            ? profile
            : PrinterProfile.Fake;
    }

    public async Task<bool> SetProfileAsync(PrinterProfile profile, CancellationToken ct = default)
    {
        await using var context = await factory.CreateDbContextAsync(ct);
        AppSettingKeyValue? row = await context.AppSettings.FindAsync([Key], ct);
        if (row is null)
        {
            context.AppSettings.Add(new AppSettingKeyValue { Key = Key, Value = profile.ToString() });
        }
        else
        {
            context.Entry(row).CurrentValues.SetValues(row with { Value = profile.ToString() });
        }

        await context.SaveChangesAsync(ct);
        return true;
    }
}
