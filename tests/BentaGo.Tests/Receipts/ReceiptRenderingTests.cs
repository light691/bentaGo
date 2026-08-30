using System.Text;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Payments;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Core.Domain.Trips;
using Xunit;

namespace BentaGo.Tests.Receipts;

/// <summary>
/// BG-007a receipt rendering: the FromSale snapshot (docs/03 §6 layout
/// values) and the ESC/POS 80 mm byte encoder (hardware-profile: 42-col,
/// UTF-8, init + cut). The golden text/bytes are independent of the encoder
/// implementation so a layout change must update the goldens on purpose.
/// </summary>
public class ReceiptRenderingTests
{
    private static Sale SampleSale()
    {
        var when = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        var sale = Sale.Create(
            new SaleId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new TripId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new PeddlerId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
            new LocationId(Guid.Parse("99999999-9999-9999-9999-999999999999")),
            "R-20260115-007",
            when,
            new UserId(Guid.Parse("44444444-4444-4444-4444-444444444444")),
            new[]
            {
                new SaleLine(
                    new SaleLineId(Guid.Parse("55555555-5555-5555-5555-555555555556")),
                    new ProductId(Guid.Parse("66666666-6666-6666-6666-666666666666")),
                    "SARDINES", "Sardines",
                    Money.FromPesos(45m), Quantity.Of(1), Money.Zero),
            },
            new[]
            {
                new PaymentLine(
                    new PaymentLineId(Guid.Parse("77777777-7777-7777-7777-777777777778")),
                    PaymentMethod.Cash, "", Money.FromPesos(45m), Money.FromPesos(50m), Money.FromPesos(5m)),
            });
        return sale;
    }

    [Fact]
    public void FromSale_CapturesLayoutValues()
    {
        ReceiptDocument doc = ReceiptDocument.FromSale(SampleSale());

        Assert.Equal("R-20260115-007", doc.Meta.ReceiptNumber);
        Assert.Single(doc.Lines);
        Assert.Equal(45m, doc.Total.ToPesos());
        Assert.Equal(45m, doc.Subtotal.ToPesos());
        Assert.Equal(Money.Zero, doc.DiscountTotal);
        Assert.Equal(5m, doc.Change.ToPesos());
        Assert.False(doc.IsReprint);
        Assert.Null(doc.ReferenceNote);
    }

    [Fact]
    public void FromSale_ReferenceNoteListsNonCashReferences()
    {
        Sale sale = SampleSale() with
        {
            Payments = new[]
            {
                new PaymentLine(
                    new PaymentLineId(Guid.Parse("77777777-7777-7777-7777-777777777779")),
                    PaymentMethod.Gcash, "GCASH-TXN-1", Money.FromPesos(45m), Money.FromPesos(45m), Money.Zero),
            },
        };

        ReceiptDocument doc = ReceiptDocument.FromSale(sale);

        Assert.Contains("GCASH-TXN-1", doc.ReferenceNote);
        Assert.DoesNotContain(doc.ReferenceNote!, "Cash");
    }

    [Fact]
    public void Encoder_EmitInitAndCutAroundText()
    {
        byte[] bytes = new EscPos80mmEncoder().Encode(ReceiptDocument.FromSale(SampleSale()));

        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes.Take(2).ToArray());
        Assert.True(bytes[^3..].SequenceEqual(new byte[] { 0x1D, 0x56, 0x42 }));
        Assert.True(bytes.Contains((byte)0x0A));
    }

    [Fact]
    public void Encoder_LayoutGolden_SimpleCashSale()
    {
        // 42-column layout: name(21) + qty right-aligned into 26 cols + money
        // right-aligned into 16 cols; TOTAL/CHANGE right-aligned to 42; the
        // receipt footer keeps the number + trip/peddler ids.
        var encoder = new EscPos80mmEncoder();
        byte[] bytes = encoder.Encode(ReceiptDocument.FromSale(SampleSale()));
        string[] lines = Encoding.UTF8.GetString(bytes)
            .Replace("\u001B@", string.Empty, StringComparison.Ordinal)
            .Replace("\u001BE\u0000", string.Empty, StringComparison.Ordinal)
            .Replace("\u001DE\u0001", string.Empty, StringComparison.Ordinal)
            .Replace("\u001DV\u0042", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string expectedLine0 =
            new string(' ', 3) + "Sardines" + new string(' ', 13) + "x1" + new string(' ', 11) + "45.00";
        Assert.Equal(42, expectedLine0.Length); // 42-col layout
        Assert.Equal(expectedLine0, lines[0]);
        string expectedTotal = new string(' ', 42 - "TOTAL 45.00".Length) + "TOTAL 45.00";
        string expectedChange = new string(' ', 42 - "CHANGE 5.00".Length) + "CHANGE 5.00";
        Assert.Equal(expectedTotal, lines[1]);
        Assert.Equal(expectedChange, lines[2]);
        Assert.Equal("R#R-20260115-007 2026-01-15 08:00", lines[3]);
        Assert.StartsWith("TRIP", lines[4]);
        Assert.StartsWith("PDL", lines[5]);
    }

    [Fact]
    public void Encoder_ReprintLabelsEveryCopy()
    {
        ReceiptDocument doc = ReceiptDocument.FromSale(SampleSale()) with { IsReprint = true };
        byte[] bytes = new EscPos80mmEncoder().Encode(doc);

        string text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("REPRINT", text, StringComparison.Ordinal);
        Assert.Contains("R-20260115-007" /* original number kept */, text, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapsAtNominalColumns()
    {
        Assert.Single(EscPos80mmEncoder.Wrap("A short line", 42));
        // An over-long unbroken word: the space at index 11 splits once, then
        // the 60-dash word wraps at the column width (42 + 18).
        IReadOnlyList<string> wrapped = EscPos80mmEncoder.Wrap("A long word " + new string('-', 60), 42);
        Assert.Equal(3, wrapped.Count);
        Assert.All(wrapped, line => Assert.True(line.Length <= 42));
    }

    [Fact]
    public void Wrap_KeepsWordBoundaries()
    {
        IReadOnlyList<string> wrapped = EscPos80mmEncoder.Wrap(
            "Sardines canned in tomato sauce 155g pack of three", 42);

        Assert.Equal(2, wrapped.Count);
        Assert.Equal("Sardines canned in tomato sauce 155g pack", wrapped[0]);
        Assert.Equal("of three", wrapped[1]);
    }

    [Fact]
    public void PadHelpers_NeverExceedWidth()
    {
        Assert.Equal(42, EscPos80mmEncoder.PadLeft("TOTAL 45.00", 42).Length);
        Assert.EndsWith("45.00", EscPos80mmEncoder.PadLeft("TOTAL 45.00", 42), StringComparison.Ordinal);
        Assert.Equal(42, EscPos80mmEncoder.PadCenter("REPRINT", 42).Length);
        Assert.StartsWith(" ", EscPos80mmEncoder.PadCenter("REPRINT", 42), StringComparison.Ordinal);
    }
}
