namespace BentaGo.App.Navigation;

/// <summary>
/// Screen view models opt out of disposal-on-evict by implementing this
/// marker (bg-004-synthesis §5.1, B-graft). BG-004: all six screens implement
/// it (retention is the default, so the POS cart and reconciliation draft
/// survive screen switches). A VM that is IDisposable but NOT retainable is
/// disposed once on evict (machinery for BG-005 admin screens).
/// </summary>
public interface IRetainableScreen
{
}