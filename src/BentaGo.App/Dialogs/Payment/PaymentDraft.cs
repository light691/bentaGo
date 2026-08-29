using BentaGo.Application.Contracts;
using BentaGo.Core.Domain;

namespace BentaGo.App.Dialogs.Payment;

/// <summary>
/// Typed payment result; Lines are the application-contract
/// <see cref="PaymentLineDraft"/>s the checkout session consumes directly,
/// and Change is non-zero for cash over-tender.
/// </summary>
public sealed record PaymentDialogResult(IReadOnlyList<PaymentLineDraft> Lines, Money Change);
