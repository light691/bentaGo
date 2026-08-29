using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BentaGo.App;
using BentaGo.App.Features.Reconciliation;
using BentaGo.App.Mvvm;
using BentaGo.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Round-5 layout-integrity regressions:
/// B - the status-bar lead segment must render the literal U+00B7 middot
///     (XAML does not decode HTML entities);
/// F - the Reconciliation payment-summary rows must never overlap (the old
///     layout stacked four label/value pairs into the same two grid cells).
/// </summary>
[Collection("AppShellSta")]
public sealed class StatusBarAndLayoutTests
{
    private readonly AppShellStaFixture _sta;

    public StatusBarAndLayoutTests(AppShellStaFixture sta) => _sta = sta;

    [Fact]
    public void StatusBarLeadSegmentUsesTheLiteralEmDash()
    {
        _sta.Run(() =>
        {
            using var session = ShellSession.Open(_sta);
            TextBlock? lead = session.Window.FindName("StatusLeadText") as TextBlock;
            Assert.NotNull(lead);
            // Round-6: the reference status strip lead reads "Offline — local
            // data" with a literal U+2014 em dash (XAML never decodes HTML
            // entities, so &mdash; would render as text).
            Assert.Equal("Offline — local data", lead.Text);
            Assert.DoesNotContain("&mdash;", lead.Text);
            Assert.DoesNotContain("&middot;", lead.Text);
            Assert.Contains('\u2014', lead.Text);
        });
    }

    [Fact]
    public void ReconciliationSummaryRowsDoNotOverlap()
    {
        _sta.Run(() =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-recon-overlap-" + Guid.NewGuid().ToString("N"));
            try
            {
                using var services = BentaGoHost.Build(
                    development: true,
                    dataRootOverride: temp,
                    extra: ObservableObjectFactory.RegisterAppServices);
                var view = new ReconciliationView
                {
                    DataContext = services.GetRequiredService<ReconciliationViewModel>(),
                };
                using var probe = new ProbeHost(view, 1092, 614);
                _sta.PumpIdle();

                // The summary card: every visible TextBlock in it must not
                // intersect any other (labels and values are separate cells;
                // any overlap is the ghosting defect from round-5 F).
                Border? card = FindCard(view, "Payment summary");
                Assert.NotNull(card);

                var bounds = new List<Rect>();
                foreach (DependencyObject node in Descendants(card))
                {
                    if (node is TextBlock { IsVisible: true } text && text.ActualWidth > 0 && text.ActualHeight > 0)
                    {
                        bounds.Add(text.TransformToAncestor(view).TransformBounds(new Rect(text.RenderSize)));
                    }
                }

                for (int i = 0; i < bounds.Count; i++)
                {
                    for (int j = i + 1; j < bounds.Count; j++)
                    {
                        Rect intersection = bounds[i];
                        intersection.Intersect(bounds[j]);
                        Assert.True(
                            intersection.Width <= 1 && intersection.Height <= 1,
                            $"summary text blocks overlap: {bounds[i]} vs {bounds[j]} (area {intersection.Width}x{intersection.Height})");
                    }
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

    private static Border? FindCard(DependencyObject root, string title)
    {
        foreach (DependencyObject node in Descendants(root))
        {
            if (node is TextBlock { Text: { } text } tb && tb.Text == title)
            {
                // Climb to the styling Border (CardStyle) that contains it.
                FrameworkElement? parent = tb.Parent as FrameworkElement;
                while (parent is not null)
                {
                    if (parent is Border border)
                    {
                        return border;
                    }

                    parent = parent.Parent as FrameworkElement;
                }
            }
        }

        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject grandchild in Descendants(child))
            {
                yield return grandchild;
            }
        }
    }

    /// <summary>Minimal real shell for status-bar assertions (no login).</summary>
    private sealed class ShellSession : IDisposable
    {
        private readonly string _tempRoot;

        private ShellSession(string tempRoot, ServiceProvider services, MainWindow window)
        {
            _tempRoot = tempRoot;
            Services = services;
            Window = window;
        }

        public ServiceProvider Services { get; }

        public MainWindow Window { get; }

        public static ShellSession Open(AppShellStaFixture sta)
        {
            string temp = Path.Combine(Path.GetTempPath(), "bentago-status-" + Guid.NewGuid().ToString("N"));
            var services = BentaGoHost.Build(
                development: true,
                dataRootOverride: temp,
                extra: ObservableObjectFactory.RegisterAppServices);
            var viewModel = services.GetRequiredService<MainViewModel>();
            var window = new MainWindow(viewModel)
            {
                Width = 1366,
                Height = 768,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 40,
            };
            window.Show();
            viewModel.Boot();
            sta.PumpIdle();
            return new ShellSession(temp, services, window);
        }

        public void Dispose()
        {
            Window.Close();
            Services.Dispose();
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}