namespace BentaGo.Infrastructure.Persistence;

/// <summary>
/// Thrown when a persisted movement row cannot be decoded into a sealed
/// domain movement (unknown kind, unknown document kind, or a payload column
/// set that violates the kind's legal shape). The store maps this to the
/// LEDGER_CORRUPT DomainError (BG-003 §8 and §9.1).
/// </summary>
public sealed class LedgerCorruptException(string message) : Exception(message);
