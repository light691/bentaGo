using System.Security.Cryptography;
using System.Text;

namespace BentaGo.Application.Import;

/// <summary>
/// Deterministic product SKU derivation for the client-mapping layer
/// (BG-005b locked decision 1). The client file has NO sku column, so every
/// row gets a stable business key here and the preview always shows it:
///
///   primary  = barcode1 digits verbatim when barcode1 is present and
///              non-empty (leading zeros preserved, no normalization);
///   fallback = "X" + first 12 hex chars of SHA-1(normalized name), where the
///              normalized name = trim + collapse internal whitespace +
///              uppercase invariant.
///
/// Digit SKUs (barcode-derived) and "X"-prefixed SKUs (name-derived) are
/// collision-free by construction because the client's barcode1 values are
/// digits. Empty names still derive (from the empty normalized name) so the
/// preview can always show a value, but the row is flagged with a
/// NAME_EMPTY error and excluded from import.
/// </summary>
public static class ProductSkuDerivation
{
    public const string HashPrefix = "X";
    public const int HashHexLength = 12;

    /// <summary>
    /// trim + collapse internal whitespace (runs collapse to one space) +
    /// uppercase invariant — the exact locked normalization used for the
    /// name-derived fallback SKU.
    /// </summary>
    public static string NormalizeName(string name)
    {
        var builder = new StringBuilder(name.Length);
        bool pendingSpace = false;
        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToUpperInvariant(c));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Derives the SKU for one client row. <paramref name="barcode1"/> is used
    /// verbatim when present and non-empty; otherwise the name-derived
    /// fallback is computed. Never returns null/empty.
    /// </summary>
    public static string Derive(string? barcode1, string name)
    {
        string? barcode = barcode1?.Trim();
        if (!string.IsNullOrEmpty(barcode))
        {
            return barcode;
        }

        byte[] hash = SHA1.HashData(Encoding.UTF8.GetBytes(NormalizeName(name)));
        string hex = Convert.ToHexString(hash).ToLowerInvariant();
        return HashPrefix + hex[..HashHexLength];
    }
}
