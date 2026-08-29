using BentaGo.Core.Common;
using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Builders;
using BentaGo.Core.Domain.Catalog;
using Xunit;

namespace BentaGo.Tests.Sales;

public class DiscountPolicyTests
{
    private static readonly DiscountRates Rates = DiscountRates.Statutory;

    private static Product Product(bool pwd = false, bool senior = false) => new(
        new ProductId(Guid.NewGuid()), "SKU", null, "P",
        null, new Category("c"), "piece", Money.FromPesos(45m), null, null, false, null, pwd, senior);

    [Fact]
    public void None_IsAlwaysAllowed_AndZero()
    {
        Result<Money> r = DiscountPolicy.Allowed(Product(), DiscountType.None, Rates, Money.FromPesos(45m), Quantity.Of(1));
        Assert.True(r.IsSuccess);
        Assert.True(r.Value.IsZero);
    }

    [Fact]
    public void Pwd_Requires_ProductFlag()
    {
        Assert.True(DiscountPolicy.Allowed(Product(pwd: true), DiscountType.Pwd, Rates, Money.FromPesos(45m), Quantity.Of(1)).IsSuccess);
        Result<Money> notAllowed = DiscountPolicy.Allowed(Product(senior: true), DiscountType.Pwd, Rates, Money.FromPesos(45m), Quantity.Of(1));
        Assert.True(notAllowed.IsFailure);
        Assert.Equal("DISCOUNT_NOT_ALLOWED", notAllowed.Errors[0].Code);
    }

    [Fact]
    public void Senior_Requires_ProductFlag()
    {
        Assert.True(DiscountPolicy.Allowed(Product(senior: true), DiscountType.Senior, Rates, Money.FromPesos(45m), Quantity.Of(1)).IsSuccess);
        Assert.True(DiscountPolicy.Allowed(Product(pwd: true), DiscountType.Senior, Rates, Money.FromPesos(45m), Quantity.Of(1)).IsFailure);
    }

    [Theory]
    [InlineData(45_00, 2, 20, 18_00)]
    [InlineData(45_00, 1, 20, 9_00)]
    [InlineData(99_50, 3, 20, 59_70)]
    public void Math_Is_Floor_Truncation_In_Centavos(long unitCents, int qty, int percent, long expectedCents)
    {
        var rates = new DiscountRates(percent, percent);
        Result<Money> r = DiscountPolicy.Allowed(
            Product(pwd: true, senior: true), DiscountType.Pwd, rates,
            new Money(unitCents), Quantity.Of(qty));
        Assert.True(r.IsSuccess);
        Assert.Equal(expectedCents, r.Value.Centavos);
    }

    [Fact]
    public void Zero_Percent_Is_Not_Allowed()
    {
        var rates = new DiscountRates(0, 20);
        Result<Money> r = DiscountPolicy.Allowed(Product(pwd: true), DiscountType.Pwd, rates, Money.FromPesos(45m), Quantity.Of(1));
        Assert.True(r.IsFailure);
        Assert.Equal("DISCOUNT_NOT_ALLOWED", r.Errors[0].Code);
    }
}
