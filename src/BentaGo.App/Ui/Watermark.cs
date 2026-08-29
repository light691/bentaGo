using System.Windows;

namespace BentaGo.App.Ui;

/// <summary>
/// Watermark / placeholder text for the shared TextBox template
/// (round-3 UX completion): the template shows the hint while the control is
/// empty, hides it as soon as the user types, and never intercepts input.
/// The hint text is a plain string attribute (allowed by the zero-literal
/// rule); the template styling lives in ScreenStyles.xaml.
/// </summary>
public static class Watermark
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Watermark), new PropertyMetadata(null));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
}
