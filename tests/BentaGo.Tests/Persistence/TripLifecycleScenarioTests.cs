using BentaGo.Application.Services;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// BG-005c golden contract for the persisted world: the SQLite trip-lifecycle
/// day (open → load → sell → close + the cancel variant with load revert)
/// renders byte-for-byte identical to the in-memory golden, both after the
/// run and after a reopen + rebuild (the fold replays the persisted stream,
/// TripCancel included).
/// </summary>
public sealed class TripLifecycleScenarioTests : PersistenceTestBase
{
    [Fact]
    public async Task PersistedTripLifecycle_MatchesInMemoryGolden_ByteForByte()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var store = host.GetRequiredService<SqliteLedgerStore>();
        string persisted = TripLifecycleScenario.Run(store).Text;

        var memory = new InMemoryLedgerStore();
        string inMemory = TripLifecycleScenario.Run(memory).Text;

        Assert.Equal(inMemory, persisted);
        Assert.NotEmpty(persisted);

        // A fresh store over the same file re-folds (TripCancel included)
        // and renders identically — the cancel survives the restart.
        var reopened = new SqliteLedgerStore(
            host.GetRequiredService<IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        Assert.Equal(persisted, TripLifecycleScenario.Render(reopened).Text);

        // And after a rebuild the render is still byte-identical.
        await reopened.RebuildAsync();
        Assert.Equal(persisted, TripLifecycleScenario.Render(reopened).Text);
    }
}
