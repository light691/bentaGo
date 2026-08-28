namespace BentaGo.Core.Domain;

/// <summary>
/// Generic key/value store persisted in the AppSettings table.
/// Placeholder only; NOT the business domain model.
/// </summary>
public sealed record AppSettingKeyValue
{
    public string Key { get; init; } = "";
    public string Value { get; init; } = "";
}
