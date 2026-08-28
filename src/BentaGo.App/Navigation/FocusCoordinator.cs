using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BentaGo.App.Ui;

namespace BentaGo.App.Navigation;

/// <summary>
/// The only UI-bound focus type (bg-004-synthesis §5.1): resolves element
/// names over the shell content subtree (matching FocusTarget.Id), defers via
/// Loaded if content is not yet realized, and logs misses. It is the single
/// funnel for both navigation focus requests and explicit VM requests, so
/// MainWindow code-behind stays free of focus logic.
/// </summary>
public sealed class FocusCoordinator : IFocusService, IDisposable
{
    private readonly INavigationService _navigation;
    private string? _pendingElement;
    private FrameworkElement? _deferredContent;

    public FocusCoordinator(INavigationService navigation)
    {
        _navigation = navigation;
        _navigation.FocusRequested += OnNavigationFocusRequested;
    }

    public event EventHandler<FocusRequest>? Requested;

    public void Request(string elementName) => Route(new FocusRequest(elementName));

    private void OnNavigationFocusRequested(object? sender, FocusRequest request) => Route(request);

    private void Route(FocusRequest request)
    {
        // The payload is observable first (unit tests capture it), then the
        // shell-side side effect happens.
        Requested?.Invoke(this, request);
        if (TryFocus(request.ElementName))
        {
            return;
        }

        _pendingElement = request.ElementName;
        DeferToCurrentContent();
    }

    public static bool TryFocus(string elementName)
    {
        if (System.Windows.Application.Current?.MainWindow is not { } window)
        {
            return false;
        }

        FrameworkElement? element = FindByFocusId(window, elementName);
        if (element is null)
        {
            System.Diagnostics.Trace.TraceWarning($"FocusTarget not resolved: {elementName}");
            return false;
        }

        element.Focus();
        Keyboard.Focus(element);
        return true;
    }

    /// <summary>Symbolic name of the currently keyboard-focused element (used by
    /// DialogService to restore focus after a modal round-trip).</summary>
    public static string? CurrentFocusedElementName()
    {
        return Keyboard.FocusedElement is DependencyObject focused ? FocusTarget.GetId(focused) : null;
    }

    private void DeferToCurrentContent()
    {
        if (System.Windows.Application.Current?.MainWindow is not MainWindow shell
            || shell.ShellContent?.Content is not FrameworkElement content)
        {
            return;
        }

        _deferredContent = content;
        content.Loaded -= OnDeferredContentLoaded;
        content.Loaded += OnDeferredContentLoaded;
    }

    private void OnDeferredContentLoaded(object? sender, RoutedEventArgs e)
    {
        if (_deferredContent is { } content)
        {
            content.Loaded -= OnDeferredContentLoaded;
            _deferredContent = null;
        }

        if (_pendingElement is { } pending)
        {
            _pendingElement = null;
            TryFocus(pending); // a second miss is simply logged by TryFocus
        }
    }

    public static FrameworkElement? FindByFocusId(DependencyObject root, string id)
    {
        if (root is FrameworkElement element && FocusTarget.GetId(element) == id)
        {
            return element;
        }

        if (root is not Visual)
        {
            return null;
        }

        for (int i = VisualTreeHelper.GetChildrenCount(root) - 1; i >= 0; i--)
        {
            FrameworkElement? found = FindByFocusId(VisualTreeHelper.GetChild(root, i), id);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public void Dispose() => _navigation.FocusRequested -= OnNavigationFocusRequested;
}