namespace BentaGo.Core.Domain;

// Branded Guid ids (docs/02: "internal UUID" is a required uniqueness
// constraint). Each domain kind gets its own wrapper type so a TripId can
// never be passed where a ProductId is expected.

public readonly record struct UserId(Guid Value);
public readonly record struct PeddlerId(Guid Value);
public readonly record struct ProductId(Guid Value);
public readonly record struct LocationId(Guid Value);
public readonly record struct TripId(Guid Value);
public readonly record struct LoadId(Guid Value);
public readonly record struct SaleId(Guid Value);
public readonly record struct SaleLineId(Guid Value);
public readonly record struct VoidId(Guid Value);
public readonly record struct ReturnId(Guid Value);
public readonly record struct AdjustmentId(Guid Value);
public readonly record struct CountId(Guid Value);
public readonly record struct CloseId(Guid Value);
public readonly record struct TransferId(Guid Value);
