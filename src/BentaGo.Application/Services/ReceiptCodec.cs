using System.Text.Json;
using System.Text.Json.Serialization;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Receipts;

namespace BentaGo.Application.Services;

/// <summary>
/// The persisted receipt snapshot: the printable model as plain JSON. A
/// retry/reprint renders from THIS snapshot, never from live state, so the
/// printed copy is byte-stable even after the store changes (docs/05
/// "render receipts from a stored model").
/// </summary>
public static class ReceiptCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ReceiptDocument document)
    {
        Snapshot snapshot = Snapshot.From(document);
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static ReceiptDocument Deserialize(string json)
    {
        Snapshot? snapshot = JsonSerializer.Deserialize<Snapshot>(json, Options)
            ?? throw new FormatException($"invalid receipt snapshot: {json[..Math.Min(json.Length, 80)]}");
        return snapshot.ToDocument();
    }

    private sealed record Snapshot(
        string ReceiptNumber,
        DateTimeOffset CreatedUtc,
        string Day,
        IReadOnlyList<LineSnapshot> Lines,
        long SubtotalCents,
        long DiscountTotalCents,
        long TotalCents,
        long ChangeCents,
        bool IsReprint,
        string? ReferenceNote,
        [property: JsonPropertyName("lineCount")] int LineCount = 0,
        string? Kind = null,
        CloseSnapshot? Close = null)
    {
        public static Snapshot From(ReceiptDocument document) => new(
            document.Meta.ReceiptNumber,
            document.Meta.CreatedUtc,
            document.Meta.Day.Value,
            document.Lines
                .Select(l => new LineSnapshot(l.Name, l.UnitPrice.Centavos, l.Quantity.Units, l.Discount.Centavos))
                .ToList(),
            document.Subtotal.Centavos,
            document.DiscountTotal.Centavos,
            document.Total.Centavos,
            document.Change.Centavos,
            document.IsReprint,
            document.ReferenceNote,
            LineCount: document.Lines.Count,
            Kind: document.Kind.ToString(),
            Close: document.Close is null ? null : CloseSnapshot.From(document.Close));

        public ReceiptDocument ToDocument() => new(
            new ReceiptMeta(
                ReceiptNumber,
                CreatedUtc,
                new DayKey(Day)),
            Lines
                .Select(l => new ReceiptLine(
                    l.Name,
                    new Money(l.UnitPriceCents),
                    Quantity.Of(l.Quantity),
                    new Money(l.DiscountCents)))
                .ToList(),
            new Money(SubtotalCents),
            new Money(DiscountTotalCents),
            new Money(TotalCents),
            new Money(ChangeCents),
            IsReprint,
            ReferenceNote,
            Kind: Kind is null ? ReceiptKind.Sale : Enum.Parse<ReceiptKind>(Kind),
            Close: Close?.ToDomain());
    }

    /// <summary>Persisted close-summary payload (money stored as centavos).</summary>
    private sealed record CloseSnapshot(
        string? StoreName,
        string? StoreAddress,
        string? StoreContact,
        string? StoreTin,
        string CloseId,
        IReadOnlyList<FundSnapshot> Funds,
        IReadOnlyList<StockSnapshot> Stocks,
        TotalsSnapshot Totals,
        string? Notes)
    {
        public static CloseSnapshot From(CloseSummary close) => new(
            close.StoreName,
            close.StoreAddress,
            close.StoreContact,
            close.StoreTin,
            close.CloseId,
            close.FundRows
                .Select(f => new FundSnapshot(f.Fund, f.Expected.Centavos, f.Counted.Centavos, f.Variance.Centavos))
                .ToList(),
            close.StockRows
                .Select(s => new StockSnapshot(
                    s.Sku, s.Product, s.ExpectedUnits, s.CountedUnits, s.QuantityVariance,
                    s.ValueVariance?.Centavos))
                .ToList(),
            new TotalsSnapshot(
                close.Totals.Sales,
                close.Totals.SalesTotal.Centavos,
                close.Totals.Returns,
                close.Totals.RefundTotal.Centavos,
                close.Totals.ExpectedCash.Centavos,
                close.Totals.CountedCash.Centavos,
                close.Totals.CashVariance.Centavos,
                close.Totals.CashLabel),
            close.Notes);

        public CloseSummary ToDomain() => new(
            StoreName,
            StoreAddress,
            StoreContact,
            StoreTin,
            CloseId,
            Funds
                .Select(f => new CloseFundRow(f.Fund, new Money(f.ExpectedCents), new Money(f.CountedCents), new Money(f.VarianceCents)))
                .ToList(),
            Stocks
                .Select(s => new CloseStockRow(
                    s.Sku, s.Product, s.ExpectedUnits, s.CountedUnits, s.QuantityVariance,
                    s.ValueCents is { } vc ? new Money(vc) : null))
                .ToList(),
            new CloseTotals(
                Totals.Sales,
                new Money(Totals.SalesTotalCents),
                Totals.Returns,
                new Money(Totals.RefundTotalCents),
                new Money(Totals.ExpectedCashCents),
                new Money(Totals.CountedCashCents),
                new Money(Totals.CashVarianceCents),
                Totals.CashLabel),
            Notes);
    }

    private sealed record FundSnapshot(string Fund, long ExpectedCents, long CountedCents, long VarianceCents);

    private sealed record StockSnapshot(
        string Sku, string Product, int ExpectedUnits, int? CountedUnits, int? QuantityVariance, long? ValueCents);

    private sealed record TotalsSnapshot(
        int Sales,
        long SalesTotalCents,
        int Returns,
        long RefundTotalCents,
        long ExpectedCashCents,
        long CountedCashCents,
        long CashVarianceCents,
        string CashLabel);

    private sealed record LineSnapshot(string Name, long UnitPriceCents, int Quantity, long DiscountCents);
}
