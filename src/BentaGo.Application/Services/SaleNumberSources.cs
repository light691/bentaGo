using BentaGo.Application.Contracts;
using BentaGo.Core.Domain.Display;
using BentaGo.Core.Domain.Documents;

namespace BentaGo.Application.Services;

/// <summary>
/// Day-global count-derived receipt numbers (D6): R-{yyyyMMdd}-{NNN} where
/// NNN = count of committed Sales whose ReceiptNumber starts with the
/// Manila-day prefix + 1, zero-padded to 3. The Sales.receipt_number UNIQUE
/// index is the collision backstop (the session retries under the same key);
/// gaps are accepted (docs/01 requires uniqueness, not gaplessness).
/// </summary>
public sealed class CommittedSaleNumberSource(ILedgerStore store) : ISaleNumberSource
{
    public string Next(DateTimeOffset nowUtc, IReadOnlyCollection<string> alreadyAttempted)
    {
        store.Bind();
        string day = ManilaDisplay.UtcAsManila(nowUtc).ToString("yyyyMMdd");
        string prefix = $"R-{day}-";
        int count = 0;
        foreach (Sale s in store.Context.Sales)
        {
            if (s.ReceiptNumber.StartsWith(prefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        string candidate;
        do
        {
            count++;
            candidate = prefix + count.ToString("D3");
        }
        while (alreadyAttempted.Contains(candidate, StringComparer.Ordinal));

        return candidate;
    }
}

/// <summary>Deterministic test double: pops from a fixed queue, then falls
/// back to day-global counting (unit-test only — never registered in the
/// production host).</summary>
public sealed class FixedNumberSource(Queue<string> queue) : ISaleNumberSource
{
    public string Next(DateTimeOffset nowUtc, IReadOnlyCollection<string> alreadyAttempted)
    {
        string candidate = queue.Dequeue();
        while (alreadyAttempted.Contains(candidate, StringComparer.Ordinal) && queue.Count > 0)
        {
            candidate = queue.Dequeue();
        }

        return candidate;
    }
}
