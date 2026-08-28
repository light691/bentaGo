namespace BentaGo.Core.Common;

/// <summary>
/// A structured functional error: machine code, human message, and the
/// optional field or path the error concerns.
/// </summary>
public sealed record DomainError(string Code, string Message, string? Field = null);
