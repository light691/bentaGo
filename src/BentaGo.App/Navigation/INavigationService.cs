namespace BentaGo.App.Navigation;

/// <summary>
/// Navigation contract (bg-004-synthesis §5.1): string-keyed activation of
/// registered screens through implicit DataTemplates, symbolic focus
/// requests only. Unit-testable without a UI thread.
/// </summary>
public interface INavigationService
{
    /// <summary>Key of the active screen, or null before the first Navigate.</summary>
    string? CurrentKey { get; }

    /// <summary>Raised after the active screen changes (drives the shell ContentControl).</summary>
    event EventHandler<NavigationEventArgs>? Navigated;

    /// <summary>Raised when focus must move; the shell resolves ElementName -&gt; element.</summary>
    event EventHandler<FocusRequest>? FocusRequested;

    /// <summary>True when the key can be navigated to (registered AND not blocked by the login gate).</summary>
    bool CanNavigate(string key);

    /// <summary>
    /// Activates the registered screen. Returns false for unknown/blocked
    /// keys (no throw, friendly to Alt+4-parked entries). Re-navigating to
    /// Current is a no-op (idempotent). Focus defaults from the registry's
    /// InitialFocusTarget.
    /// </summary>
    void Navigate(string key);

    /// <summary>Explicit symbolic focus request (dialog restore, F1 overlay return, scan re-focus).</summary>
    void RequestFocus(string elementName);

    /// <summary>
    /// Releases the pre-auth gate (called once after a successful login).
    /// Before unlock, only <see cref="ScreenKeys.Login"/> is navigable.
    /// </summary>
    void Unlock();

    /// <summary>Navigation body duration (perf probe, bg-004-synthesis §13).</summary>
    double LastNavigationMilliseconds { get; }
}