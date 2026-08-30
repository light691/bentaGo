using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Receipts;
using Microsoft.EntityFrameworkCore;

namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// EF-backed print-job store (BG-007). A job is created after the checkout
/// commits (docs/03 §7: commit first, printer reports after), so an
/// unplugged printer leaves exactly one saved sale and one recoverable job.
/// Updates go through <see cref="TryUpdateAsync"/> with the row's own
/// attempts+1 semantics so concurrent retries cannot double-print.
/// </summary>
public sealed class PrintJobStore(IDbContextFactory<BentaGoDbContext> factory) : IPrintJobStore
{
    public async Task<PrintJob> EnqueueAsync(PrintJob job, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        PrintJob? existing = await context.PrintJobs.SingleOrDefaultAsync(j => j.Id == job.Id, cancellationToken);
        if (existing is null)
        {
            context.PrintJobs.Add(job);
        }
        else
        {
            context.Entry(existing).CurrentValues.SetValues(job);
        }

        await context.SaveChangesAsync(cancellationToken);
        return job;
    }

    public async Task<PrintJob?> FindByIdAsync(PrintJobId id, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.PrintJobs.SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
    }

    public async Task<PrintJob?> FindBySaleAsync(
        SaleId saleId, bool reprintInclusive, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        List<PrintJob> rows = await context.PrintJobs
            .Where(j => j.SaleId == saleId && (reprintInclusive || j.ReprintOf == null))
            .ToListAsync(cancellationToken);
        // SQLite cannot ORDER BY DateTimeOffset inline; sort client-side.
        return rows.OrderBy(j => j.CreatedUtc).ThenBy(j => j.Id.Value).FirstOrDefault();
    }

    public async Task<IReadOnlyList<PrintJob>> ListAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        List<PrintJob> rows = await context.PrintJobs
            .Take(take)
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(j => j.CreatedUtc).ToList();
    }

    public async Task<bool> TryUpdateAsync(
        PrintJobId id,
        PrintJobStatus status,
        int attempts,
        string? lastErrorCode,
        DateTimeOffset lastAttemptAt,
        CancellationToken cancellationToken = default)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        PrintJob? row = await context.PrintJobs.SingleOrDefaultAsync(j => j.Id == id, cancellationToken);
        if (row is null)
        {
            return false;
        }

        // The tracked row stays the tracked instance; copy the new values onto
        // it (a `with` record would be a second instance with the same key).
        PrintJob updated = row with
        {
            Status = status,
            Attempts = attempts,
            LastErrorCode = lastErrorCode,
            LastAttemptAt = lastAttemptAt,
        };
        context.Entry(row).CurrentValues.SetValues(updated);
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
