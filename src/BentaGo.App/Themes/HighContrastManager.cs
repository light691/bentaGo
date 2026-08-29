using System.Windows;
using System.Windows.Threading;

namespace BentaGo.App.Themes;

/// <summary>
/// High-contrast dictionary swap (bg-004-synthesis §4.4): observes system
/// high-contrast state and inserts/removes Theme.HighContrast.xaml right
/// after Theme.xaml in Application resources. Styles consume brushes via
/// DynamicResource, so the swap re-resolves in place by construction. The
/// predicate is injectable for tests (fake predicate, no SystemParameters).
/// </summary>
public sealed class HighContrastManager : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly Func<bool> _isHighContrast;
    private readonly ResourceDictionary _highContrast;
    private readonly bool _attachSystemEvents;
    private bool _installed;
    private bool _disposed;

    public HighContrastManager(System.Windows.Application application, Func<bool> isHighContrast, bool attachSystemEvents = true)
    {
        _application = application;
        _isHighContrast = isHighContrast;
        _attachSystemEvents = attachSystemEvents;
        _highContrast = new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/BentaGo.App;component/Themes/Theme.HighContrast.xaml"),
        };

        if (_attachSystemEvents)
        {
            try
            {
                SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
            catch (Exception)
            {
                // Non-interactive hosts may not support preference events;
                // the --sim-hc flag and the explicit Apply() path still work.
                _attachSystemEvents = false;
            }
        }

        Apply();
    }

    public bool IsHighContrastInstalled => _installed;

    public void Apply()
    {
        bool highContrast = _isHighContrast();
        var merged = _application.Resources.MergedDictionaries;
        bool present = merged.Contains(_highContrast);
        if (highContrast && !present)
        {
            merged.Insert(1, _highContrast);
        }
        else if (!highContrast && present)
        {
            merged.Remove(_highContrast);
        }

        _installed = highContrast;
    }

    private void OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast) or "HighContrastKey")
        {
            Apply();
        }
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Apply));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Restore the default palette: the high-contrast dictionary must be
        // removed or every later render in the process (and any following
        // capture in a shared test host) stays system-colour collapsed.
        if (_installed)
        {
            _application.Resources.MergedDictionaries.Remove(_highContrast);
            _installed = false;
        }

        if (_attachSystemEvents)
        {
            try
            {
                SystemParameters.StaticPropertyChanged -= OnStaticPropertyChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            }
            catch (Exception)
            {
                // Best-effort teardown.
            }
        }
    }
}
