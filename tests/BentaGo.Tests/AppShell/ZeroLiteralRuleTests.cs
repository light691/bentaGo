using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BentaGo.Tests.AppShell;

/// <summary>
/// Zero-literal markup rule (bg-004-synthesis §4): the review gate greps
/// Features/ for Foreground=, Background=, FontSize=, literal positive
/// Heights, and raw Margin attributes and fails. Screens are markup +
/// bindings + style references; colors, sizes, and spacing come from tokens
/// and styles only. (Theme/Styles dictionaries are the sanctioned literal
/// holders and are not scanned.)
/// </summary>
public sealed class ZeroLiteralRuleTests
{
    private static readonly string FeaturesRoot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "BentaGo.App", "Features"));

    [Fact]
    public void FeatureMarkupContainsNoLiterals()
    {
        Assert.True(Directory.Exists(FeaturesRoot), $"Features root not found: {FeaturesRoot}");
        string[] xamlFiles = Directory.GetFiles(FeaturesRoot, "*.xaml", SearchOption.AllDirectories);
        Assert.NotEmpty(xamlFiles);

        var banned = new (string Name, Regex Regex)[]
        {
            ("Foreground=", new Regex(@"\bForeground\s*=", RegexOptions.Compiled)),
            ("Background=", new Regex(@"\bBackground\s*=", RegexOptions.Compiled)),
            ("FontSize=", new Regex(@"\bFontSize\s*=", RegexOptions.Compiled)),
            ("literal Height>0", new Regex(@"\bHeight\s*=\s*""[1-9]", RegexOptions.Compiled)),
            // Raw margin values only; token references (StaticResource/
            // DynamicResource) are the sanctioned spacing channel.
            ("raw Margin", new Regex(@"\bMargin\s*=\s*""(?!\{)", RegexOptions.Compiled)),
        };

        var violations = new List<string>();
        foreach (string file in xamlFiles)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach ((string name, Regex regex) in banned)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        string relative = Path.GetRelativePath(FeaturesRoot, file);
                        violations.Add($"{relative}:{i + 1} [{name}] {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0, "Zero-literal rule violated:\n" + string.Join("\n", violations));
    }
}