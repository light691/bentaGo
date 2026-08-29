namespace BentaGo.App.Navigation;

/// <summary>
/// VM-facing focus service (bg-004-synthesis §5.1): view models request
/// symbolic focus; unit tests assert the payload only. Implemented by the
/// shell-side FocusCoordinator, the only UI-bound focus type.
/// </summary>
public interface IFocusService
{
    /// <summary>Raised when focus must move; shell resolves ElementName -&gt; element.</summary>
    event EventHandler<FocusRequest>? Requested;

    void Request(string elementName);
}
