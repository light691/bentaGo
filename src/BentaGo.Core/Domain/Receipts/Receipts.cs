using System.Text;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Reconciliation;

namespace BentaGo.Core.Domain.Receipts;

/// <summary>
/// Receipt rendering (BG-007, docs/03 §6): an immutable snapshot of what a
/// 80 mm / 203 dpi / 42-column thermal receipt must contain, independent of
/// printer model. The app never depends on a specific printer — it encodes
/// this model through <see cref="IReceiptEncoder"/> and prints through
/// <see cref="IReceiptPrinter"/>. Money is integer centavos; the layout is
/// the nominal 42-column code page.
/// </summary>
public sealed record ReceiptDocument(
    ReceiptMeta Meta,
    IReadOnlyList<ReceiptLine> Lines,
    Money Subtotal,
    Money DiscountTotal,
    Money Total,
    Money Change,
    bool IsReprint,
    string? ReferenceNote,
    ReceiptKind Kind = ReceiptKind.Sale,
    CloseSummary? Close = null)
{
    public static ReceiptDocument FromSale(
        Sale sale,
        int lineWidth = 42)
    {
        if (lineWidth < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "receipt width must be >= 20 columns");
        }

        var lines = new List<ReceiptLine>(sale.Lines.Count);
        Money subtotal = Money.Zero;
        Money discounts = Money.Zero;
        foreach (SaleLine line in sale.Lines)
        {
            lines.Add(new ReceiptLine(
                line.ProductName,
                line.UnitPrice,
                line.Quantity,
                line.Discount));
            subtotal = subtotal.Plus(line.UnitPrice.Times(line.Quantity.Units));
            discounts = discounts.Plus(line.Discount);
        }

        string? referenceNote = null;
        if (sale.Payments.Any(p => p.Method is not (PaymentMethod.Cash) && !string.IsNullOrWhiteSpace(p.Reference)))
        {
            referenceNote = string.Join(
                " · ",
                sale.Payments
                    .Where(p => p.Method is not PaymentMethod.Cash && !string.IsNullOrWhiteSpace(p.Reference))
                    .Select(p => $"{p.Method.DisplayName()}: {p.Reference}"));
        }

        Money change = sale.Payments
            .Where(p => p.Method == PaymentMethod.Cash)
            .Aggregate(Money.Zero, (acc, p) => acc.Plus(p.Change));
        return new ReceiptDocument(
            new ReceiptMeta(sale.ReceiptNumber, sale.CreatedUtc, sale.Day),
            lines,
            subtotal,
            discounts,
            sale.Total,
            change,
            IsReprint: false,
            referenceNote);
    }

    /// <summary>
    /// Builds the close-summary snapshot from the committed reconciliation
    /// report (BG-008 Slice C). The store header is printed only when
    /// supplied; the amount is the report's own fold-derived math (Slice A),
    /// never recomputed here. The document is a <see cref="ReceiptKind.CloseSummary"/>
    /// with <see cref="Close"/> carrying the close-only facts, so the sale
    /// path (Kind stays <see cref="ReceiptKind.Sale"/>, Close stays null) is
    /// byte-stable.
    /// </summary>
    public static ReceiptDocument FromCloseSummary(
        ReconciliationReport report,
        CloseId closeId,
        DateTimeOffset closedUtc,
        string? storeName,
        string? storeAddress,
        string? storeContact,
        string? storeTin,
        string? notes,
        int lineWidth = 42)
    {
        if (lineWidth < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(lineWidth), "receipt width must be >= 20 columns");
        }

        var fundRows = new List<CloseFundRow>(report.CashRows.Count);
        foreach (CashVarianceRow cash in report.CashRows)
        {
            fundRows.Add(new CloseFundRow(
                cash.Fund == Fund.PhysicalCash ? "Physical Cash" : cash.Fund.ToString(),
                cash.Expected,
                cash.Counted,
                cash.Variance));
        }

        var stockRows = new List<CloseStockRow>(report.StockRows.Count);
        foreach (StockVarianceRow stock in report.StockRows)
        {
            stockRows.Add(new CloseStockRow(
                stock.Sku,
                stock.ProductName,
                stock.ExpectedUnits,
                stock.CountedUnits,
                stock.QuantityVariance,
                stock.ValueVariance));
        }

        ReportTotals totals = report.Totals;
        return new ReceiptDocument(
            new ReceiptMeta(
                $"CLOSE-{closeId.Value:D}",
                closedUtc,
                report.Day),
            Array.Empty<ReceiptLine>(),
            Money.Zero,
            Money.Zero,
            totals.SalesTotal,
            Money.Zero,
            IsReprint: false,
            null,
            ReceiptKind.CloseSummary,
            new CloseSummary(
                storeName,
                storeAddress,
                storeContact,
                storeTin,
                closeId.Value.ToString("D"),
                fundRows,
                stockRows,
                new CloseTotals(
                    totals.Sales,
                    totals.SalesTotal,
                    totals.Returns,
                    totals.RefundTotal,
                    totals.ExpectedCash,
                    totals.CountedCash,
                    totals.CashVariance,
                    totals.CashLabel.ToString()),
                notes));
    }
}

/// <summary>Header block: receipt number, date/time, trading day key.</summary>
public sealed record ReceiptMeta(
    string ReceiptNumber,
    DateTimeOffset CreatedUtc,
    DayKey Day);

/// <summary>One item line at the captured unit price and quantity.</summary>
public sealed record ReceiptLine(string Name, Money UnitPrice, Quantity Quantity, Money Discount);

/// <summary>The kind of document a receipt snapshot represents (BG-008 Slice C).</summary>
public enum ReceiptKind
{
    /// <summary>A committed sale (BG-007 sale path — byte-stable).</summary>
    Sale,

    /// <summary>A day close-summary (BG-008 Slice C).</summary>
    CloseSummary,
}

/// <summary>
/// The close-summary payload (BG-008 Slice C): everything the close receipt
/// renders that is not part of the sale line model. The store header is
/// printed only when supplied; <see cref="CloseId"/> is the footer identity a
/// reprint keeps across the REPRINT label.
/// </summary>
public sealed record CloseSummary(
    string? StoreName,
    string? StoreAddress,
    string? StoreContact,
    string? StoreTin,
    string CloseId,
    IReadOnlyList<CloseFundRow> FundRows,
    IReadOnlyList<CloseStockRow> StockRows,
    CloseTotals Totals,
    string? Notes);

/// <summary>One countable fund's expected/counted/variance row.</summary>
public sealed record CloseFundRow(string Fund, Money Expected, Money Counted, Money Variance);

/// <summary>One product-route cell's expected/counted/variance row.</summary>
public sealed record CloseStockRow(
    string Sku,
    string Product,
    int ExpectedUnits,
    int? CountedUnits,
    int? QuantityVariance,
    Money? ValueVariance);

/// <summary>The close rollup numbers (fold-derived by Slice A, never recomputed).</summary>
public sealed record CloseTotals(
    int Sales,
    Money SalesTotal,
    int Returns,
    Money RefundTotal,
    Money ExpectedCash,
    Money CountedCash,
    Money CashVariance,
    string CashLabel);

/// <summary>
/// The ESC/POS 80 mm byte encoder (BG-007): INIT (ESC @), bold state,
/// UTF-8 text lines wrapped at the nominal 42 columns, LF line feeds, and a
/// cut (GS V 66) at the end. The encoded stream is the same bytes a real
/// 80 mm ESC/POS printer (Epson TM-T82 class, hardware-profile) receives;
/// the fake adapter captures the document instead of bytes.
/// </summary>
public sealed class EscPos80mmEncoder : IReceiptEncoder
{
    public const int NominalColumns = 42;

    /// <summary>Column width used when rendering from JSON (unused bytes).</summary>
    public byte[] Encode(ReceiptDocument document) =>
        document.Kind == ReceiptKind.CloseSummary ? EncodeClose(document) : EncodeSale(document);

    private static byte[] EncodeSale(ReceiptDocument document)
    {
        var buffer = new MemoryStream();
        Write(buffer, 0x1B, 0x40); // ESC @ init

        foreach (ReceiptLine line in document.Lines)
        {
            WriteBold(buffer, false);
            string name = Wrap(line.Name, NominalColumns - 20)[0];
            string qtyText = $"x{line.Quantity.Units:0.##}";
            string amountText = line.UnitPrice.Times(line.Quantity.Units).Minus(line.Discount).ToPesos().ToString("0.00");
            string row = AlignEnd(PadRight(name, NominalColumns - 21) + qtyText, NominalColumns - 16) +
                PadLeft(amountText, 16);
            WriteText(buffer, row);
        }

        WriteBold(buffer, false);
        WriteText(buffer, PadLeft($"TOTAL {document.Total.ToPesos().ToString("0.00")}", NominalColumns));
        if (document.Change.Centavos > 0)
        {
            WriteText(buffer, PadLeft($"CHANGE {document.Change.ToPesos().ToString("0.00")}", NominalColumns));
        }

        foreach (ReceiptLine line in document.Lines)
        {
            if (line.Discount.Centavos > 0)
            {
                WriteText(buffer, $"DISCOUNT {line.Discount.ToPesos().ToString("0.00")}");
            }
        }

        if (!string.IsNullOrEmpty(document.ReferenceNote))
        {
            WriteBold(buffer, false);
            WriteText(buffer, document.ReferenceNote!);
        }

        WriteBold(buffer, false);
        if (document.IsReprint)
        {
            WriteBold(buffer, true);
            WriteText(buffer, PadCenter("REPRINT", NominalColumns));
            WriteBold(buffer, false);
        }

        WriteText(buffer, $"R#{document.Meta.ReceiptNumber} {document.Meta.CreatedUtc:yyyy-MM-dd HH:mm}");
        WriteText(buffer, $"DAY {document.Meta.Day}");

        buffer.WriteByte(0x1D);
        buffer.WriteByte(0x56);
        buffer.WriteByte(0x42); // GS V 66: full cut with feed
        return buffer.ToArray();
    }

    private static byte[] EncodeClose(ReceiptDocument document)
    {
        var buffer = new MemoryStream();
        Write(buffer, 0x1B, 0x40); // ESC @ init

        CloseSummary close = document.Close
            ?? throw new InvalidOperationException("a CloseSummary receipt is missing its Close payload");

        // Store header — printed only when supplied, every line padded to the
        // nominal width so the reprint layout never exceeds 42 columns.
        WriteBold(buffer, true);
        WriteText(buffer, PadCenter("CLOSE SUMMARY", NominalColumns));
        WriteBold(buffer, false);
        if (!string.IsNullOrWhiteSpace(close.StoreName))
        {
            WriteText(buffer, PadCenter(close.StoreName.Trim(), NominalColumns));
        }
        if (!string.IsNullOrWhiteSpace(close.StoreAddress))
        {
            foreach (string wrapped in Wrap(close.StoreAddress.Trim(), NominalColumns))
            {
                WriteText(buffer, PadCenter(wrapped, NominalColumns));
            }
        }
        if (!string.IsNullOrWhiteSpace(close.StoreContact))
        {
            WriteText(buffer, PadCenter(close.StoreContact.Trim(), NominalColumns));
        }
        if (!string.IsNullOrWhiteSpace(close.StoreTin))
        {
            WriteText(buffer, PadCenter($"TIN {close.StoreTin.Trim()}", NominalColumns));
        }
        WriteText(buffer, PadCenter(new string('=', NominalColumns), NominalColumns));

        WriteText(buffer, $"CLOSED {document.Meta.CreatedUtc:yyyy-MM-dd HH:mm}");
        WriteText(buffer, $"DAY {document.Meta.Day}");
        WriteText(buffer, $"CLOSE {close.CloseId}");

        if (document.IsReprint)
        {
            WriteBold(buffer, true);
            WriteText(buffer, PadCenter("REPRINT", NominalColumns));
            WriteBold(buffer, false);
        }

        // Per-fund rows (Slice A reports the countable physical-cash fund).
        foreach (CloseFundRow fund in close.FundRows)
        {
            WriteText(buffer, PadLeft($"{fund.Fund} EXPECTED {fund.Expected.ToPesos().ToString("0.00")}", NominalColumns));
            WriteText(buffer, PadLeft($"{fund.Fund} COUNTED  {fund.Counted.ToPesos().ToString("0.00")}", NominalColumns));
            WriteText(buffer, PadLeft($"{fund.Fund} VARIANCE {fund.Variance.ToPesos().ToString("0.00")}", NominalColumns));
        }

        // Stock variance summary rows (expected -> counted (variance)).
        WriteText(buffer, PadCenter("STOCK VARIANCE", NominalColumns));
        foreach (CloseStockRow stock in close.StockRows)
        {
            string counted = stock.CountedUnits?.ToString() ?? "-";
            string variance = stock.QuantityVariance?.ToString() ?? "-";
            foreach (string line in Wrap($"{stock.Sku} {stock.Product} x{stock.ExpectedUnits}->{counted} ({variance})", NominalColumns))
            {
                WriteText(buffer, line);
            }
            if (stock.ValueVariance is { } value)
            {
                WriteText(buffer, PadLeft($"VALUE VAR {value.ToPesos().ToString("0.00")}", NominalColumns));
            }
        }

        // Totals rollup.
        WriteText(buffer, PadCenter(new string('=', NominalColumns), NominalColumns));
        CloseTotals totals = close.Totals;
        WriteText(buffer, PadLeft($"SALES {totals.Sales}  TOTAL {totals.SalesTotal.ToPesos().ToString("0.00")}", NominalColumns));
        WriteText(buffer, PadLeft($"RETURNS {totals.Returns}  REFUND {totals.RefundTotal.ToPesos().ToString("0.00")}", NominalColumns));
        WriteText(buffer, PadLeft($"CASH EXPECTED {totals.ExpectedCash.ToPesos().ToString("0.00")}", NominalColumns));
        WriteText(buffer, PadLeft($"CASH COUNTED  {totals.CountedCash.ToPesos().ToString("0.00")}", NominalColumns));
        WriteText(buffer, PadLeft($"CASH VARIANCE {totals.CashVariance.ToPesos().ToString("0.00")} ({totals.CashLabel})", NominalColumns));

        if (!string.IsNullOrWhiteSpace(close.Notes))
        {
            WriteBold(buffer, false);
            WriteText(buffer, "NOTES");
            foreach (string line in Wrap(close.Notes.Trim(), NominalColumns))
            {
                WriteText(buffer, line);
            }
        }

        // Small machine-readable footer identity (reprint keeps it).
        WriteText(buffer, PadCenter(new string('-', NominalColumns), NominalColumns));
        WriteText(buffer, $"#{close.CloseId}");

        buffer.WriteByte(0x1D);
        buffer.WriteByte(0x56);
        buffer.WriteByte(0x42); // GS V 66: full cut with feed
        return buffer.ToArray();
    }

    private static void WriteBold(Stream buffer, bool on) => Write(buffer, 0x1B, 0x45, on ? (byte)1 : (byte)0);

    private static void WriteText(Stream buffer, string text)
    {
        foreach (string line in Wrap(text, NominalColumns))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            buffer.Write(bytes, 0, bytes.Length);
            buffer.WriteByte(0x0A); // LF
        }
    }

    private static void Write(Stream buffer, params byte[] bytes) => buffer.Write(bytes, 0, bytes.Length);

    /// <summary>Word-wraps to width columns; no split of an over-long word.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        var result = new List<string>();
        string remaining = text;
        while (remaining.Length > width)
        {
            int cut = remaining.LastIndexOf(' ', width, width);
            if (cut <= 0)
            {
                cut = width;
            }

            result.Add(remaining[..cut].TrimEnd());
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            result.Add(remaining);
        }

        return result;
    }

    public static string PadLeft(string text, int width) =>
        text.Length >= width ? text[^width..] : text.PadLeft(width);

    public static string PadRight(string text, int width) =>
        text.Length >= width ? text[..width] : text.PadRight(width);

    public static string PadCenter(string text, int width) =>
        text.Length >= width ? text[..width] : text.PadLeft((width + text.Length) / 2).PadRight(width);

    public static string AlignEnd(string text, int width) => PadLeft(text, width);
}

/// <summary>Encodes a receipt document to printer bytes.</summary>
public interface IReceiptEncoder
{
    byte[] Encode(ReceiptDocument document);
}

/// <summary>Printer-agnostic print target (BG-007 hardware-profile contract).</summary>
public interface IReceiptPrinter
{
    Task<ReceiptPrintResult> PrintAsync(ReceiptDocument document, CancellationToken cancellationToken = default);
}

/// <summary>PRINTED with the byte count, or FAILED with a diagnostic code.</summary>
public sealed record ReceiptPrintResult(PrintJobStatus Status, int BytesWritten, string? ErrorCode, DateTimeOffset AtUtc)
{
    public static ReceiptPrintResult Printed(int bytes, DateTimeOffset atUtc) =>
        new(PrintJobStatus.Printed, bytes, null, atUtc);

    public static ReceiptPrintResult Failed(string errorCode, DateTimeOffset atUtc) =>
        new(PrintJobStatus.Failed, 0, errorCode, atUtc);
}

public enum PrintJobStatus
{
    /// <summary>Queued but not yet sent to the printer.</summary>
    Queued,

    /// <summary>The bytes were written successfully.</summary>
    Printed,

    /// <summary>The write failed (unplugged, offline); the sale is already saved.</summary>
    Failed,
}

/// <summary>
/// A stored print job: the printable snapshot of one sale plus attempt
/// history. Retrying the SAME job is idempotent (never a second sale);
/// reprints clone the job with IsReprint. Reference: docs/05 BG-007 "print
/// job persistence", docs/03 §7 "printer adapter reports status after
/// commit; reprint is a separate command".
/// </summary>
public sealed record PrintJob(
    PrintJobId Id,
    SaleId SaleId,
    string ReceiptNumber,
    string ReceiptDocumentJson,
    PrintJobStatus Status,
    int Attempts,
    string? LastErrorCode,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastAttemptAt,
    PrintJobId? ReprintOf)
{
    public bool IsReprint => ReprintOf is not null;
}

/// <summary>Branded print-job id.</summary>
public readonly record struct PrintJobId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}
