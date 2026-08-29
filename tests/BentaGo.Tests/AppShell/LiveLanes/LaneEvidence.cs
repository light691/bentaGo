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

    /// <summary>
/// Saves a review-pack PNG and returns whether the rail pixel at (10,400)
/// rendered the brand navy (#01274F) - the rail must never silently ship
/// light (a mounted high-contrast dictionary collapses it to the system
/// control colour). Dialog captures (no rail) pass verifyRail: false.
/// </summary>
    public static bool SaveReviewPng(string fileName, int widthPx, int heightPx, Visual visual, bool verifyRail = true, bool centerVisual = false)
    {
        Directory.CreateDirectory(ReviewDir);
        var bitmap = new RenderTargetBitmap(widthPx, heightPx, 96, 96, PixelFormats.Pbgra32);
        RenderTargetBitmap? renderedVisual = null;
        if (!centerVisual)
        {
            renderedVisual = new RenderTargetBitmap(widthPx, heightPx, 96, 96, PixelFormats.Pbgra32);
            renderedVisual.Render(visual);
        }

        // Compose every review capture onto the page surface. The live WPF
        // window can be a few pixels shorter than the fixed evidence canvas;
        // leaving the remainder unpainted produces a misleading black band.
        var canvas = new DrawingVisual();
        using (DrawingContext drawing = canvas.RenderOpen())
        {
            var pageBrush = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("Brush.Surface.Page");
            drawing.DrawRectangle(pageBrush, null, new Rect(0, 0, widthPx, heightPx));

            if (renderedVisual is not null)
            {
                drawing.DrawImage(renderedVisual, new Rect(0, 0, widthPx, heightPx));
            }
            else if (visual is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                double left = centerVisual ? Math.Max(0, (widthPx - element.ActualWidth) / 2) : 0;
                double top = centerVisual ? Math.Max(0, (heightPx - element.ActualHeight) / 2) : 0;
                drawing.DrawRectangle(new VisualBrush(visual), null,
                    new Rect(left, top, element.ActualWidth, element.ActualHeight));
            }
        }
        bitmap.Render(canvas);

        bool railNavy = true;
        if (verifyRail && widthPx > 40 && heightPx > 420)
        {
            railNavy = SampleBgra(bitmap, 10, 400, out byte r, out byte g, out byte b)
                && Math.Abs(r - 0x01) <= 12
                && Math.Abs(g - 0x27) <= 12
                && Math.Abs(b - 0x4F) <= 12;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(ReviewDir, fileName));
        encoder.Save(stream);
        return railNavy;
    }

    private static bool SampleBgra(RenderTargetBitmap bitmap, int x, int y, out byte r, out byte g, out byte b)
    {
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        b = pixel[0];
        g = pixel[1];
        r = pixel[2];
        return true;
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
