using BentaGo.Application.Contracts;

namespace BentaGo.Infrastructure.Printing;

/// <summary>
/// In-memory ring of raw scanner inputs (BG-007 lane 5). The scan box reports
/// the UNTRIMMED text the wedge produced — the exact suffix included — and
/// the matched product, so diagnostics show what actually arrived.
/// </summary>
public sealed class ScannerDiagnostics : IScannerDiagnostics
{
    private const int Capacity = 20;

    private readonly object _gate = new();
    private readonly List<ScanInputEntry> _entries = new(Capacity);
    private int _ordinal;

    public void RecordScan(string rawText, string? matchedProduct, DateTimeOffset atUtc)
    {
        lock (_gate)
        {
            _entries.Add(new ScanInputEntry(++_ordinal, rawText, SuffixOf(rawText), matchedProduct, atUtc));
            while (_entries.Count > Capacity)
            {
                _entries.RemoveAt(0);
            }
        }
    }

    /// <summary>The exact trailing suffix of the raw input ('' when none).</summary>
    public static string SuffixOf(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        if (raw.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        if (raw.EndsWith('\n'))
        {
            return "\n";
        }

        return raw.EndsWith('\r') ? "\r" : "";
    }

    public IReadOnlyList<ScanInputEntry> Recent
    {
        get
        {
            lock (_gate)
            {
                return _entries.OrderByDescending(e => e.Ordinal).ToArray();
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }
}
