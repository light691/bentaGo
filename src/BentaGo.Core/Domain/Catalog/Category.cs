namespace BentaGo.Core.Domain.Catalog;

/// <summary>
/// A product category as supplied by the client file (BG-005b). The name is
/// preserved as-is after trimming — no case normalization is applied because
/// brands live in Category (locked mapping decision). The default selling
/// unit is derived per category; today every category maps to "piece".
/// </summary>
public sealed record Category(string Name)
{
    /// <summary>Category-default selling unit per the BG-005b v2 mapping.</summary>
    public string DefaultUnit => "piece";
}