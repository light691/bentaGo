using System.Windows;
using System.Windows.Threading;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// xunit collection marker: the AppShell STA tier runs on a single STA
/// thread with one Application, serialized against all other collections
/// (bg-004-synthesis 4.6 / 10). No new packages - hand-rolled.
/// </summary>
[CollectionDefinition("AppShellSta", DisableParallelization = true)]
public sealed class AppShellStaCollection : ICollectionFixture<AppShellStaFixture>
{
}

/// <summary>
/// One dedicated STA thread running a real WPF dispatcher loop hosting a
/// single <see cref="System.Windows.Application"/> with the BG-004 resource
/// merge installed in App.xaml order. Tests marshal work onto it with
/// <see cref="Run"/>/<see cref="Run{T}"/>; work is executed on the STA
/// thread and waits synchronously on the caller side (no queue, no
/// self-deadlock).
/// </summary>
public sealed class AppShellStaFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private Exception? _fault;

    public AppShellStaFixture()
    {
        _thread = new Thread(RunLoop)
        {
            Name = "AppShellSta",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_fault is not null)
        {
            throw new InvalidOperationException("AppShellStaFixture failed to start.", _fault);
        }
    }

    /// <summary>
    /// The one Application instance; non-null after the fixture is ready.
    /// </summary>
    public System.Windows.Application Application { get; private set; } = null!;

    /// <summary>
    /// Merged-dictionary order mirrors App.xaml at rest (bg-004-synthesis 4):
    /// Theme, ScreenStyles, Validation, DataTemplates. The HighContrast
    /// override is NOT merged statically (it would override the theme in
    /// normal mode): the runtime manager inserts it after Theme only when
    /// high contrast is active, and the parity test loads it separately.
    /// </summary>
    private static readonly string[] DictionaryUris =
    [
        "pack://application:,,,/BentaGo.App;component/Themes/Theme.xaml",
        "pack://application:,,,/BentaGo.App;component/Styles/ScreenStyles.xaml",
        "pack://application:,,,/BentaGo.App;component/Styles/Validation.xaml",
        "pack://application:,,,/BentaGo.App;component/Views/Converters.xaml",
        "pack://application:,,,/BentaGo.App;component/Views/DataTemplates.xaml",
    ];

    private void RunLoop()
    {
        try
        {
            Application = new System.Windows.Application
            {
                // A probe window Close() must never shut the shared
                // Application down (default is OnLastWindowClose); the
                // fixture owns the lifecycle and ends the loop explicitly.
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            var merged = new ResourceDictionary();
            foreach (string uri in DictionaryUris)
            {
                merged.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(uri) });
            }

            Application.Resources = merged;
            _ready.Set();

            // Standard WPF frame loop; ended by InvokeShutdown on dispose.
            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _fault = ex;
            _ready.Set();
        }
    }

    public void Run(Action action) => Application.Dispatcher.Invoke(action);

    public T Run<T>(Func<T> func) => Application.Dispatcher.Invoke(func);

    /// <summary>
    /// Pumps the STA dispatcher until idle, so windows created by the action
    /// finish Loaded/rendering before assertions run. Safe to call from
    /// inside a <see cref="Run"/> action (nested dispatch frame).
    /// </summary>
    public void PumpIdle() => Application.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));

    public void Dispose()
    {
        try
        {
            Application.Dispatcher.Invoke(new Action(Application.Shutdown));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Dispatcher already torn down; nothing left to stop.
        }

        if (_thread.IsAlive)
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }
}