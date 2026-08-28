using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BentaGo.Tests.AppShell.LiveLanes;

/// <summary>
/// Lane evidence writer (bg-004-synthesis §11): renders a PNG per lane and
/// writes a JSON verdict + a summary index, all under
/// artifacts/verification/bg-004/&lt;head&gt;/. The review-pack PNGs land in
/// artifacts/review/. Live-lane tests are env-gated (BENTAGO_LIVE_LANES=1)
/// so the mandated --filter AppShell run stays headless.
/// </summary>
internal static class LaneEvidence
{
    public const string VerdictPass = "PASS";
    public const string VerdictFail = "FAIL";
    public const string VerdictOperator = "OPERATOR";

    private static readonly string ArtifactsRoot =
        Environment.GetEnvironmentVariable("BENTAGO_ARTIFACTS") ?? @"C:\BentaGo\artifacts";

    private static readonly string Head =
        Environment.GetEnvironmentVariable("BENTAGO_HEAD") ?? "current";

    public static string VerificationDir { get; } =
        Path.Combine(ArtifactsRoot, "verification", "bg-004", Head);

    public static string ReviewDir { get; } =
        Path.Combine(ArtifactsRoot, "review");

    private static readonly List<LaneVerdict> Verdicts = new();

    public static void SavePng(string fileName, int widthPx, int heightPx, Visual visual, double dpiX = 96, double dpiY = 96)
    {
        Directory.CreateDirectory(VerificationDir);
        var bitmap = new RenderTargetBitmap(widthPx, heightPx, dpiX, dpiY, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(VerificationDir, fileName));
        encoder.Save(stream);
    }

    public static void SaveReviewPng(string fileName, int widthPx, int heightPx, Visual visual)
    {
        Directory.CreateDirectory(ReviewDir);
        var bitmap = new RenderTargetBitmap(widthPx, heightPx, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(ReviewDir, fileName));
        encoder.Save(stream);
    }

    public static async Task WriteVerdictAsync(string lane, string name, string verdict, string reason, string? evidenceFile = null)
    {
        Directory.CreateDirectory(VerificationDir);
        var verdictRow = new LaneVerdict(lane, name, verdict, reason, evidenceFile);
        Verdicts.Add(verdictRow);
        await File.WriteAllTextAsync(
            Path.Combine(VerificationDir, $"{lane}.json"),
            JsonSerializer.Serialize(verdictRow, new JsonSerializerOptions { WriteIndented = true }));
        await WriteSummaryAsync();
    }

    public static async Task WriteTraceAsync(string text)
    {
        Directory.CreateDirectory(VerificationDir);
        await File.WriteAllTextAsync(Path.Combine(VerificationDir, "bg-004-navigation.trace.txt"), text);
    }

    private static async Task WriteSummaryAsync()
    {
        Directory.CreateDirectory(VerificationDir);
        await File.WriteAllTextAsync(
            Path.Combine(VerificationDir, "SUMMARY.json"),
            JsonSerializer.Serialize(Verdicts, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed record LaneVerdict(string Lane, string Name, string Verdict, string Reason, string? EvidenceFile);
}