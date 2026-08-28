namespace BentaGo.Core.Domain.Display;

/// <summary>
/// Pure presentation conversions. Storage is UTC everywhere; clocks and
/// receipts display in Asia/Manila. No I/O in here.
/// </summary>
public static class ManilaDisplay
{
    private static readonly TimeZoneInfo Manila = TimeZoneInfo.FindSystemTimeZoneById("Asia/Manila");

    public static DateTimeOffset UtcAsManila(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Manila);

    public static string Money(Money money) =>
        money.ToPesos().ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-PH"));

    public static string DateTime(DateTimeOffset utc) => UtcAsManila(utc).ToString("yyyy-MM-dd HH:mm");
}
