using System.Text;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Trips;

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
    string? ReferenceNote)
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
            new ReceiptMeta(sale.ReceiptNumber, sale.CreatedUtc, sale.Peddler, sale.Trip),
            lines,
            subtotal,
            discounts,
            sale.Total,
            change,
            IsReprint: false,
            referenceNote);
    }
}

/// <summary>Header block: receipt number, date/time, peddler/trip ids.</summary>
public sealed record ReceiptMeta(
    string ReceiptNumber,
    DateTimeOffset CreatedUtc,
    PeddlerId Peddler,
    TripId Trip);

/// <summary>One item line at the captured unit price and quantity.</summary>
public sealed record ReceiptLine(string Name, Money UnitPrice, Quantity Quantity, Money Discount);

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
    public byte[] Encode(ReceiptDocument document)
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
        WriteText(buffer, $"TRIP {document.Meta.Trip.Value} PDL {document.Meta.Peddler.Value}");

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
