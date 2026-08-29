namespace BentaGo.Application.Import;

/// <summary>
/// A parsed product CSV: the source name, header names as found in the file,
/// and the data rows. Produced by the Infrastructure CSV parser (TRIM +
/// quote handling + no-BOM + CRLF tolerance) and consumed by the pure
/// mapping layer; explicit row numbers let errors point at the file line.
/// </summary>
public sealed record CsvProductFile(
    string SourceName,
    IReadOnlyList<string> HeaderNames,
    IReadOnlyList<CsvProductRow> Rows);

/// <summary>One data row: its 1-based file line number and raw field values (already split/trimmed).</summary>
public sealed record CsvProductRow(int LineNumber, IReadOnlyList<string> Fields);