using System.IO;
using System.Windows.Input;
using BentaGo.App;
using BentaGo.App.Mvvm;
using BentaGo.App.Navigation;
using BentaGo.App.Navigation.Shortcuts;
using BentaGo.App.Ui;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Shortcut conflict tests (bg-004-synthesis §6.3, §10): per-scope gesture
/// uniqueness, no NumPad, no mnemonics in any XAML, text-editing keys safe,
/// Reserved/Future entries catalogued but unbound, and (STA subcase) the
/// instantiated MainWindow's InputBindings equal the catalog's window-bound
/// Active entries exactly.
/// </summary>
public sealed class ShortcutConflictTests : IDisposable
{
    private readonly AppShellTestHost _host;

    public ShortcutConflictTests() => _host = new AppShellTestHost(seedAdmin: true);

    public void Dispose() => _host.Dispose();

    private static readonly string AppRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BentaGo.App"));

    [Fact]
    public void NoDuplicateGesturePerScopeAmongActiveEntries()
    {
        var duplicates = ShortcutCatalog.All
            .Where(e => e.Status == ShortcutStatus.Active)
            .GroupBy(e => (e.Scope, e.Key, e.Modifiers))
            .Where(g => g.Count() > 1)
            .ToArray();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void NoNumericKeypadGestures()
    {
        // No NumPad gestures at all (docs/03 §4, §6.3.2); the Alt+1..5 nav
        // chords use Key.D1..D5 which are the top-row digits, not NumPad.
        Assert.DoesNotContain(ShortcutCatalog.All, e => e.Key.ToString().StartsWith("NumPad", StringComparison.Ordinal));
        Assert.DoesNotContain(ShortcutCatalog.All, e => !e.Modifiers.HasFlag(ModifierKeys.Alt) && e.Key is Key.D0 or Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6 or Key.D7 or Key.D8 or Key.D9);
    }

    [Fact]
    public void NoAccessKeysOrUnderscoreMnemonicsInAnyXaml()
    {
        string[] xamlFiles = Directory.GetFiles(AppRoot, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        var violations = new List<string>();
        foreach (string file in xamlFiles)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"\bAccessKey\s*=")
                    || System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"Content=""[^""]*_[A-Za-z]"))
                {
                    violations.Add($"{Path.GetRelativePath(AppRoot, file)}:{i + 1} {lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0, "mnemonic violations:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void ReservedAndFutureEntriesNeverBound()
    {
        foreach (ShortcutEntry entry in ShortcutCatalog.All.Where(e => e.Status != ShortcutStatus.Active))
        {
            Assert.DoesNotContain(ShortcutCatalog.WindowBindings, e => e.Id == entry.Id);
        }
    }

    [Fact]
    public void TextEditingKeysAreNotRebound()
    {
        Key[] protectedKeys = [Key.C, Key.V, Key.X, Key.Z, Key.Home, Key.End, Key.Left, Key.Right, Key.Up, Key.Down, Key.Back];
        foreach (ShortcutEntry entry in ShortcutCatalog.WindowBindings)
        {
            Assert.DoesNotContain(protectedKeys, k => k == entry.Key);
        }
    }

    [Fact]
    public void ParkedEntriesAreFlaggedReservedOrFuture()
    {
        var parked = new Dictionary<string, ShortcutStatus>
        {
            ["lock"] = ShortcutStatus.Reserved,               // Ctrl+Alt+L
            ["backup"] = ShortcutStatus.Reserved,             // Ctrl+Alt+B
            ["hold-cart"] = ShortcutStatus.Future,            // F4
            ["refresh"] = ShortcutStatus.Future,              // F5
            ["new-sale"] = ShortcutStatus.Future,             // Ctrl+N
            ["reset"] = ShortcutStatus.Future,                // Ctrl+R
        };

        foreach ((string id, ShortcutStatus status) in parked)
        {
            Assert.Equal(status, ShortcutCatalog.All.Single(e => e.Id == id).Status);
        }
    }

    [Fact]
    public void EveryWindowBoundEntryHasACommandMapping()
    {
        var main = _host.Services.GetRequiredService<MainViewModel>();
        foreach (ShortcutEntry entry in ShortcutCatalog.WindowBindings)
        {
            var mapping = main.CommandMapping(entry.Id);
            Assert.NotNull(mapping.Command);
        }
    }

    [Fact]
    public void WindowBindingsAreExactlyTheActiveNonContextualEntries()
    {
        var expected = ShortcutCatalog.All
            .Where(e => e.Status == ShortcutStatus.Active
                        && e.Scope is not (ShortcutCatalog.ScopeContextual or ShortcutCatalog.ScopePaymentDialog))
            .Select(e => e.Id)
            .OrderBy(id => id);
        Assert.Equal(expected, ShortcutCatalog.WindowBindings.Select(e => e.Id).OrderBy(id => id));
    }
}

/// <summary>
/// STA subcase: instantiating MainWindow produces exactly the catalog's
/// window-bound Active entries as InputBindings (binding/catalog parity,
/// bg-004-synthesis §6.3.8).
/// </summary>
[Collection("AppShellSta")]
public sealed class MainWindowBindingParityTests
{
    private readonly AppShellStaFixture _sta;

    public MainWindowBindingParityTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void MainWindowInputBindingsEqualTheCatalogExactly()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-parity-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);
                var viewModel = services.GetRequiredService<MainViewModel>();
                var window = new MainWindow(viewModel);
                _sta.PumpIdle();

                var catalogGestures = ShortcutCatalog.WindowBindings
                    .Select(e => (e.Key, e.Modifiers))
                    .OrderBy(t => t.Key)
                    .ThenBy(t => t.Modifiers)
                    .ToArray();
                var windowGestures = window.InputBindings
                    .OfType<KeyBinding>()
                    .Select(b => (b.Key, b.Modifiers))
                    .OrderBy(t => t.Key)
                    .ThenBy(t => t.Modifiers)
                    .ToArray();

                Assert.Equal(catalogGestures.Length, window.InputBindings.Count);
                Assert.Equal(catalogGestures, windowGestures);

                // Every command must be a live MainViewModel command (map
                // command reference identity through CommandMapping).
                foreach (ShortcutEntry entry in ShortcutCatalog.WindowBindings)
                {
                    var mapping = viewModel.CommandMapping(entry.Id);
                    var binding = window.InputBindings.OfType<KeyBinding>()
                        .SingleOrDefault(b => b.Key == entry.Key && b.Modifiers == entry.Modifiers);
                    Assert.NotNull(binding);
                    Assert.Same(mapping.Command, binding.Command);
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        });
    }
}
