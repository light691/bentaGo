using BentaGo.Application.Services;
using BentaGo.Infrastructure.Configuration;
using BentaGo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.Persistence;

/// <summary>
/// C14 golden contract for the persisted world: the SQLite two-peddler day
/// renders byte-for-byte identical to the in-memory golden both after the run
/// and after a reopen (the fold replays the persisted stream).
/// </summary>
public sealed class PersistedGoldenTests : PersistenceTestBase
{
    [Fact]
    public async Task PersistedScenario_MatchesInMemoryGolden_ByteForByte()
    {
        string root = NewRoot();
        ServiceProvider host = NewHost(root);
        await host.GetRequiredService<DbInitializer>().EnsureCreatedOrMigrateAsync();

        var store = host.GetRequiredService<SqliteLedgerStore>();
        string persisted = TwoPeddlersScenario.Run(store).Text;

        var memory = new InMemoryLedgerStore();
        string inMemory = TwoPeddlersScenario.Run(memory).Text;

        Assert.Equal(inMemory, persisted);
        Assert.NotEmpty(persisted);

        // A fresh store over the same file re-folds and renders identically.
        var reopened = new SqliteLedgerStore(
            host.GetRequiredService<BentaGo.Infrastructure.Configuration.IDataPathProvider>(),
            host.GetRequiredService<DebugOptions>());
        Assert.Equal(persisted, TwoPeddlersScenario.Render(reopened).Text);

        // And after a rebuild the render is still byte-identical.
        await reopened.RebuildAsync();
        Assert.Equal(persisted, TwoPeddlersScenario.Render(reopened).Text);
    }
}
