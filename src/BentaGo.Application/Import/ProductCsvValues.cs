using System.Globalization;
using BentaGo.Core.Domain;

namespace BentaGo.Application.Import;

/// <summary>
/// Pure value parsers for the client-mapping layer. Every parse is
/// culture-explicit (invariant) and boundary-strict: money must be a
/// non-negative decimal with at most 2 decimals, dates are M/D/YYYY (with an
/// optional clock suffix, matching the client's "6/18/2026 12:00:00 AM"
/// cells). These functions never throw; a parse failure is a per-row error.
/// </summary>
public static class ProductCsvValues
{
    /// <summary>
    /// Parses a money cell into <see cref="Money"/> (integer centavos, at most
    /// 2 decimals, non-negative). The parse is STRICT: the decimal point is
    /// the only allowed separator — no thousands/group separators (commas are
    /// rejected, so "12,5" / "1,234" never silently become 125 / 1234), no
    /// signs (negative is rejected too), no exponents, no currency. A bare
    /// ".50" is a valid 0.50. Surrounding whitespace is trimmed first (the
    /// CSV parser already trims fields). Empty input is false (the
    /// retail-empty case is its own row error upstream).
    /// </summary>
    public static bool TryParseMoney(string raw, out Money money)
    {
        money = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        // NumberStyles.AllowDecimalPoint ONLY: rejects commas (AllowThousands),
        // signs, exponents, and currency symbols by construction.
        if (!decimal.TryParse(raw.Trim(), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out decimal value))
        {
            return false;
        }

        if (value < 0m || decimal.Round(value, 2) != value)
        {
            return false;
        }

        money = Money.FromPesos(value);
        return true;
    }

    /// <summary>
    /// Parses an expiry cell as a calendar date with the locked explicit
    /// culture/shape: M/D/YYYY under the invariant culture, tolerating the
    /// client's trailing "12:00:00 AM" clock text. ISO or day-first dates are
    /// rejected.
    /// </summary>
    public static bool TryParseExpiryDate(string raw, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string[] formats = { "M/d/yyyy", "M/d/yyyy h:mm:ss tt", "M/d/yyyy h:mm tt" };
        if (!DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out DateTime parsed))
        {
            return false;
        }

        date = DateOnly.FromDateTime(parsed);
        return true;
    }

    /// <summary>Parses a boolean cell ("True"/"False", invariant, case-insensitive).</summary>
    public static bool TryParseBool(string raw, out bool value) =>
        bool.TryParse(raw, out value);

    /// <summary>
    /// "Present-and-nonzero" for the PWD/senior flag derivations (locked): a
    /// boolean cell is present when parsed true; a percentage cell is present
    /// when its numeric value is non-zero. Anything else is false — the flags
    /// are derived, so an odd cell is never a row error (raw values are shown
    /// in the preview).
    /// </summary>
    public static bool PresentAndNonZero(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (TryParseBool(trimmed, out bool flag))
        {
            return flag;
        }

        return decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            && value != 0m;
    }
}