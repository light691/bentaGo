namespace BentaGo.Core.Domain.Ledger;

/// <summary>
/// Typed link from a movement to the document that caused it. Each sealed
/// kind pins the document type for the movement kinds that legally use it,
/// so a SaleIssueRevoke cannot reference a goods receipt.
/// </summary>
public abstract record DocumentRef;

public sealed record GoodsRef(GoodsId Id) : DocumentRef;

public sealed record SaleRef(SaleId Id) : DocumentRef;

public sealed record VoidRef(VoidId Id) : DocumentRef;

public sealed record ReturnRef(ReturnId Id) : DocumentRef;

public sealed record AdjustmentRef(AdjustmentId Id) : DocumentRef;

public sealed record CountRef(CountId Id) : DocumentRef;

public sealed record FundRef(FundEntryId Id) : DocumentRef;
