using System.Text.Json;
using System.Text.Json.Serialization;
using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Documents;
using BentaGo.Core.Domain.Identity;
using BentaGo.Core.Domain.Receipts;
using BentaGo.Core.Domain.Trips;

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
        Guid Peddler,
        Guid Trip,
        IReadOnlyList<LineSnapshot> Lines,
        long SubtotalCents,
        long DiscountTotalCents,
        long TotalCents,
        long ChangeCents,
        bool IsReprint,
        string? ReferenceNote,
        [property: JsonPropertyName("lineCount")] int LineCount = 0)
    {
        public static Snapshot From(ReceiptDocument document) => new(
            document.Meta.ReceiptNumber,
            document.Meta.CreatedUtc,
            document.Meta.Peddler.Value,
            document.Meta.Trip.Value,
            document.Lines
                .Select(l => new LineSnapshot(l.Name, l.UnitPrice.Centavos, l.Quantity.Units, l.Discount.Centavos))
                .ToList(),
            document.Subtotal.Centavos,
            document.DiscountTotal.Centavos,
            document.Total.Centavos,
            document.Change.Centavos,
            document.IsReprint,
            document.ReferenceNote);

        public ReceiptDocument ToDocument() => new(
            new ReceiptMeta(
                ReceiptNumber,
                CreatedUtc,
                new PeddlerId(Peddler),
                new TripId(Trip)),
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
            ReferenceNote);
    }

    private sealed record LineSnapshot(string Name, long UnitPriceCents, int Quantity, long DiscountCents);
}
