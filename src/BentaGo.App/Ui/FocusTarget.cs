using System.Windows;

namespace BentaGo.App.Ui;

/// <summary>
/// Symbolic focus addressing (bg-004-synthesis §5.1): views declare elements
/// with FocusTarget.Id (stable name) and FocusTarget.IsInitial (focus on
/// arrival). Focus is ALWAYS addressed symbolically, so requests survive view
/// recreation and are unit-testable without a UI thread. The FocusCoordinator
/// resolves Id -&gt; element over the shell content subtree.
/// </summary>
public static class FocusTarget
{
    public static readonly DependencyProperty IdProperty =
        DependencyProperty.RegisterAttached(
            "Id", typeof(string), typeof(FocusTarget), new PropertyMetadata(null));

    public static readonly DependencyProperty IsInitialProperty =
        DependencyProperty.RegisterAttached(
            "IsInitial", typeof(bool), typeof(FocusTarget), new PropertyMetadata(false));

    public static string? GetId(DependencyObject element) => (string?)element.GetValue(IdProperty);

    public static void SetId(DependencyObject element, string? value) => element.SetValue(IdProperty, value);

    public static bool GetIsInitial(DependencyObject element) => (bool)element.GetValue(IsInitialProperty);

    public static void SetIsInitial(DependencyObject element, bool value) => element.SetValue(IsInitialProperty, value);
}