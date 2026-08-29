using System.Text;
using BentaGo.Application.Import;

namespace BentaGo.Infrastructure.Import;

/// <summary>
/// Robust product CSV parser (BG-005b Commit 2): reads a UTF-8 stream (BOM
/// stripped if present — the client file itself has no BOM), splits lines on
/// CRLF/LF, and splits each line into fields with quote-shape handling. A
/// leading quote opens an RFC-4180-style quoted field (commas allowed
/// inside, "" escapes a quote); a quote appearing MID-field is kept as a
/// literal character — the client file has 7 such rows (inch-mark names like
/// "GILAS PAPER PLATE 9\""). Every field is trimmed; fully blank lines are
/// skipped. Header validation and column-order tolerance live in
/// <see cref="ProductCsvProfile"/> / <see cref="ProductCsvMapper"/>.
/// </summary>
public static class ProductCsvParser
{
    public static CsvProductFile Parse(Stream stream, string sourceName)
    {
        string text = ReadUtf8(stream);
        string[] lines = SplitLines(text);

        var headerNames = new List<string>();
        var rows = new List<CsvProductRow>();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
            {
                continue; // skip blank lines (none in the client file)
            }

            IReadOnlyList<string> fields = SplitFields(line);
            if (headerNames.Count == 0)
            {
                headerNames.AddRange(fields);
                continue;
            }

            rows.Add(new CsvProductRow(i + 1, fields));
        }

        return new CsvProductFile(sourceName, headerNames, rows);
    }

    private static string ReadUtf8(Stream stream)
    {
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();

        // Strip a UTF-8 BOM if present (client file has none; tolerance).
        int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
    }

    private static string[] SplitLines(string text)
    {
        // Normalize CRLF (and lone CR) to LF, tolerate a missing trailing
        // newline, then split.
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static IReadOnlyList<string> SplitFields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"'); // "" escape inside a quoted field
                        i++;
                        continue;
                    }

                    inQuotes = false;
                    continue;
                }

                current.Append(c);
                continue;
            }

            if (c == '"')
            {
                if (current.Length == 0)
                {
                    inQuotes = true; // quote at field start opens a quoted field
                }
                else
                {
                    current.Append(c); // mid-field quote (7-row style inch marks)
                }

                continue;
            }

            if (c == ',')
            {
                fields.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        fields.Add(current.ToString().Trim());
        return fields;
    }
}