namespace BentaGo.App.Data;

/// <summary>
/// Read-only scenario data seam (bg-004-synthesis §12). BG-005 replaces the
/// implementation with a service-backed provider behind the SAME interface;
/// screens never know. Nothing in BentaGo.App besides ScenarioDataSource
/// references the BG-002 scenario or the in-memory store.
/// </summary>
public interface IAppDataSource
{
    /// <summary>Immutable snapshot; screens bind once per navigation.</summary>
    AppSnapshot Current { get; }

    /// <summary>BG-005: repository change notifications (post-checkout, post-close).</summary>
    event EventHandler? Changed;
}
