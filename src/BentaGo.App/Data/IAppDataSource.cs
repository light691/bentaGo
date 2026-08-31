namespace BentaGo.App.Data;

/// <summary>
/// Read-only screen-data seam (bg-004-synthesis §12): the implementation is
/// the live SQLite-backed <see cref="SqliteAppDataSource"/> (BG-P1d) —
/// screens never know where the fold was served from. Nothing in BentaGo.App
/// seeds or keeps an in-memory store.
/// </summary>
public interface IAppDataSource
{
    /// <summary>Immutable snapshot; screens bind once per navigation.</summary>
    AppSnapshot Current { get; }

    /// <summary>BG-005: repository change notifications (post-checkout, post-close).</summary>
    event EventHandler? Changed;
}
