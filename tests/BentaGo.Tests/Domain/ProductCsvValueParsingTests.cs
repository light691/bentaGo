using BentaGo.Application.Import;
using BentaGo.Core.Domain;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// Pure value parsing at the client-mapping boundary: money must be
/// non-negative with at most 2 decimals; dates are M/D/YYYY under the
/// invariant culture (with an optional clock suffix, matching the client's
/// "6/18/2026 12:00:00 AM" cells); booleans are True/False. All parses are
/// culture-explicit — nothing depends on the host locale.
/// </summary>
public sealed class ProductCsvValueParsingTests
{
    [Theory]
    [InlineData("39.00", 3900)]
    [InlineData("0.00", 0)]
    [InlineData("65.5", 6550)]      // one decimal normalizes to centavos
    [InlineData(" 29.96 ", 2996)]   // surrounding whitespace tolerated
    public void Money_TwoDecimals_ParsesToCentavos(string raw, long centavos)
    {
        Assert.True(ProductCsvValues.TryParseMoney(raw, out Money money));
        Assert.Equal(centavos, money.Centavos);
    }

    [Theory]
    [InlineData("65.005")]     // 3 decimals — contract violation
    [InlineData("12,5")]       // comma decimal — not the contract shape
    [InlineData("forty")]      // not a number
    [InlineData("-1.00")]      // negative — pricing is never negative
    [InlineData("")]           // empty is not a parse (caller flags retail-empty)
    [InlineData("   ")]
    public void Money_Invalid_ReturnsFalse(string raw)
    {
        Assert.False(ProductCsvValues.TryParseMoney(raw, out _));
    }

    [Theory]
    [InlineData("6/18/2026")]
    [InlineData("6/18/2026 12:00:00 AM")]
    [InlineData("12/7/2025")]
    [InlineData(" 7/28/2025 12:00:00 AM ")]
    public void ExpiryDate_MDYyyy_InvariantCulture_Parses(string raw)
    {
        Assert.True(ProductCsvValues.TryParseExpiryDate(raw, out DateOnly date));
        Assert.True(date.Year >= 2020); // sanity: a real-looking calendar date
    }

    [Theory]
    [InlineData("18/6/2026")]     // day-first is not the locked shape
    [InlineData("2026-06-18")]    // ISO is not the locked shape
    [InlineData("13/40/2026")]    // impossible month/day
    [InlineData("2/30/2026")]     // February 30
    [InlineData("not a date")]
    [InlineData("")]
    public void ExpiryDate_NotMDYyyy_ReturnsFalse(string raw)
    {
        Assert.False(ProductCsvValues.TryParseExpiryDate(raw, out _));
    }

    [Fact]
    public void ExpiryDate_SpecificValue_RoundTrips()
    {
        Assert.True(ProductCsvValues.TryParseExpiryDate("6/18/2026 12:00:00 AM", out DateOnly date));
        Assert.Equal(new DateOnly(2026, 6, 18), date);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    public void Bool_TrueFalse_Invariant(string raw, bool expected)
    {
        Assert.True(ProductCsvValues.TryParseBool(raw, out bool value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Bool_Garbage_ReturnsFalse()
    {
        Assert.False(ProductCsvValues.TryParseBool("yes", out _));
        Assert.False(ProductCsvValues.TryParseBool("", out _));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("20.00", true)]
    [InlineData("0.00", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    public void PresentAndNonZero_FlagCells_DerivesSupport(string raw, bool expected)
    {
        Assert.Equal(expected, ProductCsvValues.PresentAndNonZero(raw));
    }
}