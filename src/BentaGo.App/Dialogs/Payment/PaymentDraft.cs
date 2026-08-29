using BentaGo.Core.Domain;
using BentaGo.Core.Domain.Payments;

namespace BentaGo.App.Dialogs.Payment;

/// <summary>
/// One tender line produced by the payment dialog
/// (bg-004-synthesis §7.1). BG-005 maps drafts to NewPaymentLines.
/// </summary>
public sealed record PaymentLineDraft(PaymentMethod Method, Money Amount, string Reference);

/// <summary>Typed payment result; Change is non-zero for cash over-tender.</summary>
public sealed record PaymentDialogResult(IReadOnlyList<PaymentLineDraft> Lines, Money Change);
