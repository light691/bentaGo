namespace BentaGo.App.Navigation;

/// <summary>
/// One registry row: screen key -&gt; view model type -&gt; initial focus target
/// (bg-004-synthesis §5.1). The registry is the only VM -&gt; key map; no view
/// knows another view's name.
/// </summary>
public sealed record ScreenRegistration(string Key, Type ViewModelType, string InitialFocusTarget);
