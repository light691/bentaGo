namespace BentaGo.Core.Domain.Payments;

/// <summary>
/// How a customer paid. <see cref="Credit"/> is utang: nothing moves into a
/// fund, it is a receivable only (pay-downs post FundIn; remitted later).
/// </summary>
public enum PaymentMethod
{
    Cash,
    Gcash,
    Maya,
    BankTransfer,
    Credit,
}

/// <summary>
/// A money pool the ledger can post into. Credit has no fund by design.
/// </summary>
public enum Fund
{
    PhysicalCash,
    Gcash,
    Maya,
    Bank,
}

public static class PaymentMethodExtensions
{
    /// <summary>
    /// The fund a payment method posts into, or null for Credit.
    /// </summary>
    public static Fund? FundOf(this PaymentMethod method) =>
        method switch
        {
            PaymentMethod.Cash => Fund.PhysicalCash,
            PaymentMethod.Gcash => Fund.Gcash,
            PaymentMethod.Maya => Fund.Maya,
            PaymentMethod.BankTransfer => Fund.Bank,
            _ => null,
        };

    public static string DisplayName(this PaymentMethod method) =>
        method switch
        {
            PaymentMethod.Cash => "Cash",
            PaymentMethod.Gcash => "GCash",
            PaymentMethod.Maya => "Maya",
            PaymentMethod.BankTransfer => "Bank",
            PaymentMethod.Credit => "Utang",
            _ => method.ToString(),
        };
}
