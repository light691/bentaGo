using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BentaGo.App.Ui;

namespace BentaGo.App.Navigation;

/// <summary>
/// The only UI-bound focus type (bg-004-synthesis §5.1): resolves element
/// names over the shell content subtree (matching FocusTarget.Id), defers via
/// Loaded if content is not yet realized, and retries until BOTH the target
/// view is realized AND the window is really keyboard-active. Round-2 fixes:
///  - requests arriving before the view (or activation) are remembered and
///    re-armed on every trigger (content change, content Loaded, window
///    Activated) - never dropped;
///  - keyboard focus is only claimed when the window is active AND
///    Keyboard.FocusedElement really became the target;
///  - on window re-activation the last successfully focused element is
///    restored.
/// </summary>
public sealed class FocusCoordinator : IFocusService, IDisposable
{
    private readonly INavigationService _navigation;

    private string? _pendingElement;
    private string? _lastFocusedElement;
    private MainWindow? _resolvedShell;
    private FrameworkElement? _hookedContent;
    private bool _wired;
    private bool _disposed;

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
        EnsureShellHooks();
        if (TryFocus(request.ElementName))
        {
            return;
        }

        _pendingElement = request.ElementName;
        RetryPending();
    }

    /// <summary>
    /// Claims keyboard focus for a symbolic target. Returns true only when
    /// the window is active AND Keyboard.FocusedElement really became the
    /// target; otherwise the request stays pending for a later retry.
    /// </summary>
    public bool TryFocus(string elementName)
    {
        if (System.Windows.Application.Current?.MainWindow is not { IsActive: true } window)
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
        if (ReferenceEquals(Keyboard.FocusedElement, element))
        {
            _pendingElement = null;
            _lastFocusedElement = elementName;
            return true;
        }

        return false;
    }

    /// <summary>Symbolic name of the currently keyboard-focused element (used by
    /// DialogService to restore focus after a modal round-trip).</summary>
    public static string? CurrentFocusedElementName()
    {
        return Keyboard.FocusedElement is DependencyObject focused ? FocusTarget.GetId(focused) : null;
    }

    private void EnsureShellHooks()
    {
        if (_wired || _disposed)
        {
            return;
        }

        if (System.Windows.Application.Current?.MainWindow is not MainWindow shell)
        {
            return;
        }

        _resolvedShell = shell;
        shell.Activated += OnShellActivated;
        DependencyPropertyDescriptor
            .FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
            .AddValueChanged(shell.ShellContent, OnShellContentChanged);
        _wired = true;
    }

    private void OnShellActivated(object? sender, EventArgs e)
    {
        RetryPending();

        // Re-activation: restore the last focused element (but not when focus
        // already sits on an element with its own id).
        if (_pendingElement is null
            && _lastFocusedElement is { } last
            && CurrentFocusedElementName() != last)
        {
            TryFocus(last);
        }
    }

    private void OnShellContentChanged(object? sender, EventArgs e) => RetryPending();

    private void OnContentLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement loaded && ReferenceEquals(loaded, _hookedContent))
        {
            loaded.Loaded -= OnContentLoaded;
            _hookedContent = null;
        }

        RetryPending();
    }

    /// <summary>
    /// Re-arms the pending request: try now, and if the view (or activation)
    /// is still missing, re-hang the Loaded hook and schedule one deferred
    /// re-check at Loaded priority so the retry survives either race.
    /// </summary>
    private void RetryPending()
    {
        if (_pendingElement is not { } pending)
        {
            return;
        }

        if (TryFocus(pending))
        {
            return;
        }

        HangRetryOnCurrentContent();
        if (_resolvedShell is not null)
        {
            _resolvedShell.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (_pendingElement is { } again)
                {
                    TryFocus(again);
                }
            }));
        }
    }

    private void HangRetryOnCurrentContent()
    {
        if (_resolvedShell?.ShellContent?.Content is not FrameworkElement content)
        {
            return; // content not realized yet; the Content-changed hook re-arms when it lands
        }

        if (ReferenceEquals(_hookedContent, content))
        {
            return;
        }

        if (_hookedContent is not null)
        {
            _hookedContent.Loaded -= OnContentLoaded;
        }

        _hookedContent = content;
        content.Loaded += OnContentLoaded;
        if (content.IsLoaded)
        {
            OnContentLoaded(content, new RoutedEventArgs(ContentControl.LoadedEvent));
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigation.FocusRequested -= OnNavigationFocusRequested;
        if (_resolvedShell is not null && _wired)
        {
            _resolvedShell.Activated -= OnShellActivated;
            DependencyPropertyDescriptor
                .FromProperty(ContentControl.ContentProperty, typeof(ContentControl))
                .RemoveValueChanged(_resolvedShell.ShellContent, OnShellContentChanged);
        }

        if (_hookedContent is not null)
        {
            _hookedContent.Loaded -= OnContentLoaded;
        }
    }
}