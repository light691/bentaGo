namespace BentaGo.Application.Import;

/// <summary>
/// The 17-column client product profile (BG-005b source of truth). Header
/// validation is tolerant to column ORDER by header name — each canonical
/// column must appear exactly once, in any position; unknown extra columns
/// are a whole-file error (v1 contract: extra columns are rejected, not
/// ignored). Column names are matched case-insensitively.
/// </summary>
public static class ProductCsvProfile
{
    public const string Name = "name";
    public const string Description = "description";
    public const string Category = "Category";
    public const string RpInclusive = "rp_inclusive";
    public const string WpInclusive = "wp_inclusive";
    public const string Barcode1 = "barcode1";
    public const string Barcode2 = "barcode2";
    public const string Warehouse = "Warehouse";
    public const string SDiscR = "s_discR";
    public const string SDiscPwdSc = "s_discPWD_SC";
    public const string SPwdScPerc = "s_PWD_SC_perc";
    public const string SDiscAth = "s_discAth";
    public const string SAskQty = "s_ask_qty";
    public const string StockQty = "stock_qty";
    public const string Cost = "cost";
    public const string HasExpiry = "has_expiry";
    public const string ExpirationDate = "expiration_date";

    /// <summary>The canonical 17 columns in client-file order.</summary>
    public static readonly IReadOnlyList<string> Columns = new[]
    {
        Name, Description, Category, RpInclusive, WpInclusive, Barcode1, Barcode2,
        Warehouse, SDiscR, SDiscPwdSc, SPwdScPerc, SDiscAth, SAskQty, StockQty,
        Cost, HasExpiry, ExpirationDate,
    };

    public const int ColumnCount = 17;

    /// <summary>
    /// Maps a header row (file order) to canonical column positions (profile
    /// order). Returns null when the file header does not contain exactly the
    /// 17 canonical columns (missing, unknown, or duplicated entries).
    /// </summary>
    public static IReadOnlyList<int>? ResolveHeader(IReadOnlyList<string> headerNames)
    {
        if (headerNames.Count != ColumnCount)
        {
            return null;
        }

        var byName = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headerNames.Count; i++)
        {
            string name = headerNames[i].Trim();
            if (!byName.TryGetValue(name, out List<int>? positions))
            {
                positions = new List<int>();
                byName[name] = positions;
            }

            positions.Add(i);
        }

        var result = new List<int>(ColumnCount);
        foreach (string canonical in Columns)
        {
            if (!byName.TryGetValue(canonical, out List<int>? positions) || positions.Count != 1)
            {
                return null;
            }

            result.Add(positions[0]);
        }

        return result;
    }
}
