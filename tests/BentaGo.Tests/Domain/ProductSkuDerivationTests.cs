using BentaGo.Application.Import;
using Xunit;

namespace BentaGo.Tests.Domain;

/// <summary>
/// SKU derivation (BG-005b locked decision 1): barcode1 digits verbatim when
/// present, else "X" + first 12 hex of SHA-1 of the normalized name
/// (trim + collapse internal whitespace + uppercase invariant). Digit SKUs and
/// "X"-prefixed SKUs are collision-free by construction.
/// </summary>
public sealed class ProductSkuDerivationTests
{
    [Fact]
    public void BarcodePresent_IsVerbatimDigits_LeadingZerosPreserved()
    {
        Assert.Equal("4801234567890", ProductSkuDerivation.Derive(" 4801234567890 ", "Some Name"));
        Assert.Equal("0123456789012", ProductSkuDerivation.Derive("0123456789012", "Another"));
    }

    [Fact]
    public void BarcodeBlankOrNull_ComputesNameDerivedFallback()
    {
        string sku = ProductSkuDerivation.Derive("   ", "Del Monte ACE Pineapple Juice 220ml");
        Assert.StartsWith("X", sku);
        Assert.Equal(1 + ProductSkuDerivation.HashHexLength, sku.Length);
        Assert.Matches("^X[0-9a-f]{12}$", sku);
    }

    [Fact]
    public void NormalizeName_TrimsCollapsesWhitespace_UppercasesInvariant()
    {
        Assert.Equal("DEL MONTE ACE PINEAPPLE JUICE 220ML",
            ProductSkuDerivation.NormalizeName("  Del Monte   ACE  Pineapple Juice 220ml  "));
        Assert.Equal("", ProductSkuDerivation.NormalizeName("   "));
    }

    [Fact]
    public void EquivalentNames_DeriveSameSku()
    {
        string spaced = ProductSkuDerivation.Derive(null, "  Del   Monte   ACE  ");
        string messy = ProductSkuDerivation.Derive(null, "del monte ace");
        Assert.Equal(spaced, messy);
        Assert.Equal("X50511b07bdde", spaced); // golden: SHA-1("DEL MONTE ACE")
    }

    [Fact]
    public void DifferentNames_DeriveDifferentSkus()
    {
        Assert.NotEqual(
            ProductSkuDerivation.Derive(null, "Del Monte ACE Pineapple Juice 220ml"),
            ProductSkuDerivation.Derive(null, "Del Monte Sweetened Pineapple Juice 220ml"));
    }

    [Fact]
    public void PrefixCollisionFreedom_DigitSkuNeverEqualsXPrefixed()
    {
        // Any barcode-derived SKU is pure digits, any name-derived SKU starts
        // with "X", so the two families cannot collide (locked decision 1).
        string barcodeSku = ProductSkuDerivation.Derive("4801234567890", "whatever");
        string nameSku = ProductSkuDerivation.Derive(null, "whatever");
        Assert.Matches("^[0-9]+$", barcodeSku);
        Assert.StartsWith("X", nameSku);
        Assert.NotEqual(barcodeSku, nameSku);
    }

    [Fact]
    public void Deterministic_SameInputSameSku()
    {
        Assert.Equal(
            ProductSkuDerivation.Derive("4801234567890", "Coke Mismo 1.5L"),
            ProductSkuDerivation.Derive("4801234567890", "Coke Mismo 1.5L"));
    }
}