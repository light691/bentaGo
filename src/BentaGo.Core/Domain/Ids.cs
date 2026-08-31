namespace BentaGo.Core.Domain;

// Branded Guid ids (docs/02: "internal UUID" is a required uniqueness
// constraint). Each domain kind gets its own wrapper type so a ProductId can
// never be passed where a UserId is expected.

public readonly record struct UserId(Guid Value)
{
    /// <summary>
    /// Sentinel actor for audit rows that cannot be attributed to a real
    /// account (e.g. a failed login with an unknown username). Never a real
    /// user (BG-005a decision).
    /// </summary>
    public static UserId Anonymous => new(Guid.Empty);
}

public readonly record struct ProductId(Guid Value);
public readonly record struct LocationId(Guid Value);
public readonly record struct SaleId(Guid Value);
public readonly record struct SaleLineId(Guid Value);
public readonly record struct VoidId(Guid Value);
public readonly record struct ReturnId(Guid Value);
public readonly record struct AdjustmentId(Guid Value);
public readonly record struct CountId(Guid Value);
public readonly record struct CloseId(Guid Value);
public readonly record struct DayId(Guid Value);
public readonly record struct GoodsId(Guid Value);
public readonly record struct FundEntryId(Guid Value);
public readonly record struct PaymentLineId(Guid Value);

/// <summary>
/// Client-supplied idempotency key for a checkout (BG-003 D1): the same key
/// re-submitted returns the SAME receipt reference and creates ONE sale. A
/// branded Guid so a checkout key can never be passed where a SaleId or
/// PaymentLineId is expected.
/// </summary>
public readonly record struct CheckoutKey(Guid Value);

/// <summary>
/// The store trading day key: the Asia/Manila local calendar date
/// (yyyy-MM-dd) a document belongs to. One store, one open day at a time;
/// every sale/receipt/correction is stamped with the day it happened on.
/// </summary>
public readonly record struct DayKey(string Value)
{
    private static readonly TimeZoneInfo Manila = LoadManila();

    public override string ToString() => Value;

    /// <summary>Converts a UTC instant to its Asia/Manila local day key.</summary>
    public static DayKey FromUtc(DateTimeOffset utc)
    {
        DateTime local = TimeZoneInfo.ConvertTime(utc, Manila).DateTime;
        return new DayKey(local.ToString("yyyy-MM-dd"));
    }

    private static TimeZoneInfo LoadManila()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows id fallback (older ICU-less hosts).
            return TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
        }
    }
}

/// <summary>
/// The single store location (BG-P1 pivot: one store warehouse — no routes,
/// no multi-location transfers). All stock and fund movements carry
/// this constant.
/// </summary>
public static class StoreLocations
{
    public static readonly LocationId Warehouse = new(Guid.Parse("00000000-0000-0000-0000-0000000000f1"));
}
