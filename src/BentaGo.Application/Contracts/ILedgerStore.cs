using BentaGo.Core.Common;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;

namespace BentaGo.Application.Contracts;

public sealed record AppendReceipt(long FirstSequence, long LastSequence);

/// <summary>
/// The single mutation seam. <see cref="Append"/> runs the state's guard per
/// movement, assigns global monotonic sequences in batch order, and advances
/// the fold. BG-003 implements this contract with one SQLite transaction, so
/// "checkout is one database transaction" becomes a storage detail behind an
/// unchanged public surface.
/// </summary>
public interface ILedgerStore
{
    LedgerContext Context { get; }
    LedgerState State { get; }
    IReadOnlyList<Movement> Movements { get; }

    Result<AppendReceipt> Append(IReadOnlyList<Movement> movements);

    void RecordAudit(AuditEvent audit);

    IReadOnlyList<AuditEvent> Audits { get; }
}
