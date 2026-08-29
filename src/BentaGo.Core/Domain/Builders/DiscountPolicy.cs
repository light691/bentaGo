using BentaGo.Core.Common;
using BentaGo.Core.Domain.Catalog;

namespace BentaGo.Core.Domain.Builders;

/// <summary>
/// The permitted discount set (0001-5: nothing else exists). F7 cycles
/// None → Pwd → Senior → None; one type per line, never stacked. The
/// <see cref="SaleBuilder"/> persists only the discount MONEY; the type is
/// derivable at read time from the product flags + policy, so the type is
/// not persisted to schema in BG-006 (a cheap later migration if BG-008
/// needs per-type reporting).
/// </summary>
public enum DiscountType
{
    None,
    Pwd,
    Senior,
}

/// <summary>
/// Percentage rates live at the boundary, NOT in the model (docs/01: the
/// percentages stay in the CSV import preview). Statutory defaults: PWD 20%
/// (RA 10754), Senior 20% (RA 9994). Overridable when a settings-backed
/// override lands (documented follow-up — percentages must never be
/// caller-supplied per line).
/// </summary>
public sealed record DiscountRates(int PwdPercent, int SeniorPercent)
{
    public static DiscountRates Statutory { get; } = new(20, 20);

    public static int PercentOf(DiscountType type, DiscountRates rates) =>
        type switch
        {
            DiscountType.Pwd => rates.PwdPercent,
            DiscountType.Senior => rates.SeniorPercent,
            _ => 0,
        };
}

/// <summary>
/// Pure, deterministic, integer-centavos discount math and policy.
/// amount = floor(unitPrice.Centavos × qty × percent / 100) — truncation
/// toward zero, never negative, exact for whole-peso statutory amounts
/// (4500 × 20 / 100 == 900).
/// </summary>
public static class DiscountPolicy
{
    /// <summary>
    /// Allowed iff type == None (zero) or the product's flag matches the type
    /// and the rate is positive. Any other nonzero candidate →
    /// DISCOUNT_NOT_ALLOWED.
    /// </summary>
    public static Result<Money> Allowed(
        Product product, DiscountType type, DiscountRates rates,
        Money unitPrice, Quantity quantity)
    {
        if (type == DiscountType.None)
        {
            return Result<Money>.Ok(Money.Zero);
        }

        bool flag = type switch
        {
            DiscountType.Pwd => product.SupportsPwdDiscount,
            DiscountType.Senior => product.SupportsSeniorDiscount,
            _ => false,
        };

        int percent = DiscountRates.PercentOf(type, rates);
        if (!flag || percent <= 0)
        {
            return Result<Money>.Fail(new DomainError(
                "DISCOUNT_NOT_ALLOWED", $"{type} discount is not allowed for this product", product.Sku));
        }

        long raw = unitPrice.Centavos * quantity.Units * percent / 100;
        if (raw <= 0)
        {
            return Result<Money>.Fail(new DomainError(
                "DISCOUNT_NOT_ALLOWED", $"{type} discount computes to zero", product.Sku));
        }

        return Result<Money>.Ok(new Money(raw));
    }
}
