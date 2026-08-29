using BentaGo.Application.Services;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Ledger;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// RecordAudit identity semantics on the IN-MEMORY store (BG-005a): each
/// RecordAudit call assigns the next incremental identity and returns it —
/// the -1 placeholder is never stored. Golden scenario rendering is
/// unaffected because audits render by message only.
/// </summary>
public sealed class AuthAuditIdentityTests
{
    private static readonly UserId Actor = new(Guid.NewGuid());
    private static readonly DateTimeOffset When = new(2026, 8, 29, 8, 0, 0, TimeSpan.Zero);

    private static AuditEvent NewAudit(string message) =>
        new(-1, Actor, When, "AUTH.TEST", message, Ref: null);

    [Fact]
    public void RecordAudit_ReturnsIncrementingIdentities_StartingAtOne()
    {
        var memory = new InMemoryLedgerStore();

        long one = memory.RecordAudit(NewAudit("one"));
        long two = memory.RecordAudit(NewAudit("two"));

        Assert.Equal(1, one);
        Assert.Equal(2, two);
        Assert.Equal(2, memory.Audits.Count);
        Assert.Equal(1, memory.Audits[0].Sequence);
        Assert.Equal(2, memory.Audits[1].Sequence);
        Assert.Equal("one", memory.Audits[0].Message);
        Assert.Equal(Actor, memory.Audits[0].Actor);
    }

    [Fact]
    public void FreshStore_RecordAudit_StartsAtOneAgain()
    {
        var first = new InMemoryLedgerStore();
        Assert.Equal(1, first.RecordAudit(NewAudit("a")));

        var second = new InMemoryLedgerStore();
        Assert.Equal(1, second.RecordAudit(NewAudit("b")));
        Assert.Equal(2, first.RecordAudit(NewAudit("c"))); // first store keeps its own counter
    }

    [Fact]
    public void BatchAppendedAudits_KeepStagedSequences_RecordAuditCounterIsIndependent()
    {
        var store = new InMemoryLedgerStore();
        var loadId = new LoadId(Guid.NewGuid());
        var product = new ProductId(Guid.NewGuid());
        var location = new LocationId(Guid.NewGuid());
        store.RegisterLoad(new StockLoad(loadId, "P-OPEN", default, Array.Empty<StockLoadLine>(), Actor, When));
        store.Bind();

        // A real commit with a batch audit: the in-memory store stages the
        // -1 placeholder as given (only the DB write gains identity, BG-005a).
        var append = store.Append(new BentaGo.Application.Contracts.AppendBatch(
            new BentaGo.Core.Domain.Ledger.Movement[]
            {
                Moves.OpeningStock(0, When, Actor, new LoadRef(loadId), product, location, Quantity.Of(10)),
            },
            Audits: new[] { NewAudit("batch-audit") }));
        Assert.True(append.IsSuccess, append.Errors.FirstOrDefault()?.Code ?? "append failed");

        var batchAudit = Assert.Single(store.Audits);
        Assert.Equal(-1, batchAudit.Sequence); // staged as given, no DB identity on the in-memory store

        long one = store.RecordAudit(NewAudit("standalone"));
        Assert.Equal(1, one); // the RecordAudit counter is independent of batch audits
        Assert.Equal(2, store.Audits.Count);
        Assert.Equal(1, store.Audits[1].Sequence);
    }
}
