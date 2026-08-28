namespace BentaGo.App.Navigation;

/// <summary>
/// Symbolic focus target (bg-004-synthesis §5.1): focus is ALWAYS addressed
/// by the element's stable name (FocusTarget.Id), never by a direct element
/// reference, so requests survive view recreation and are unit-testable
/// without a UI thread.
/// </summary>
public sealed record FocusRequest(string ElementName);