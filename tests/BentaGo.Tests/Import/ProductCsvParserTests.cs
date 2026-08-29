using System.IO;
using System.Text;
using BentaGo.Application.Import;
using BentaGo.Infrastructure.Import;
using Xunit;

namespace BentaGo.Tests.Import;

/// <summary>
/// Robust product CSV parsing (BG-005b Commit 2): UTF-8 with BOM tolerance,
/// CRLF/LF, TRIM, and quote-shape handling — a leading quote opens an
/// RFC-4180-style quoted field, a mid-field quote is a literal character
/// (the client file's 7 inch-mark rows like "GILAS PAPER PLATE 9\"").
/// </summary>
public sealed class ProductCsvParserTests
{
    private const string Header =
        "name,description,Category,rp_inclusive,wp_inclusive,barcode1,barcode2,Warehouse," +
        "s_discR,s_discPWD_SC,s_PWD_SC_perc,s_discAth,s_ask_qty,stock_qty,cost,has_expiry,expiration_date";

    private static CsvProductFile Parse(string text)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(text);
        using var stream = new MemoryStream(bytes);
        return ProductCsvParser.Parse(stream, "test.csv");
    }

    [Fact]
    public void CrlfAndLf_BothTolerated_NoBom()
    {
        // Mixed line endings in ONE file: header ends with \r\n, the first
        // data row ends with \n, the second with \r\n — the parser must
        // handle all three and must report the PHYSICAL CSV line number
        // (header = line 1, so the first data row is line 2, the second 3).
        string mixed = Header + "\r\n" + ValidRow("a", "35.00") + "\n"
            + ValidRow("b", "39.00") + "\r\n";
        CsvProductFile file = Parse(mixed);

        Assert.Equal(2, file.Rows.Count);
        Assert.Equal("a", file.Rows[0].Fields[0]);
        Assert.Equal(2, file.Rows[0].LineNumber); // header=1, first data row=2
        Assert.Equal("b", file.Rows[1].Fields[0]);
        Assert.Equal(3, file.Rows[1].LineNumber); // second data row=3
    }

    [Fact]
    public void bom_IsStripped()
    {
        string text = "\uFEFF" + Header + "\n" + ValidRow("a", "35.00") + "\n";
        CsvProductFile file = Parse(text);

        Assert.Equal(17, file.HeaderNames.Count);
        Assert.Equal("name", file.HeaderNames[0]);
        Assert.Single(file.Rows);
    }

    [Fact]
    public void MissingTrailingNewline_LastRowStillParsed()
    {
        string text = Header + "\n" + ValidRow("a", "35.00") + "\n" + ValidRow("b", "39.00");
        Assert.Equal(2, Parse(text).Rows.Count);
    }

    [Fact]
    public void BlankLines_Skipped()
    {
        string text = Header + "\n\n" + ValidRow("a", "35.00") + "\n\n";
        Assert.Single(Parse(text).Rows);
    }

    [Fact]
    public void QuotedField_WithEmbeddedComma_IsOneField()
    {
        string row = "\"Name, With Comma\",desc,Cat,35.00,0.00,4806515910543,,Stock,False,False,0.00,False,True,0.00,20.00,False,6/18/2026 12:00:00 AM";
        CsvProductFile file = Parse(Header + "\n" + row + "\n");

        CsvProductRow parsed = Assert.Single(file.Rows);
        Assert.Equal(17, parsed.Fields.Count);
        Assert.Equal("Name, With Comma", parsed.Fields[0]);
    }

    [Fact]
    public void EscapedDoubleQuote_InsideQuotedField_BecomesLiteralQuote()
    {
        string row = "\"Binder Clip 1\"\" Black\",desc,COLGATE,3.00,0.00,4806525912394,,Stock,True,False,0.00,False,False,0.00,1.50,False,12/6/2025 12:00:00 AM";
        CsvProductFile file = Parse(Header + "\n" + row + "\n");

        CsvProductRow parsed = Assert.Single(file.Rows);
        Assert.Equal("Binder Clip 1\" Black", parsed.Fields[0]);
    }

    [Fact]
    public void MidFieldQuote_InchMarkStyle_IsLiteralCharacter()
    {
        // The client file's 7 quote rows are NOT quoted fields — the quote is
        // mid-field (inch marks). The parser must keep it literally.
        string row = "GILAS PAPER PLATE 9\",GILAS PAPER PLATE 9\",HOME ESSENTIALS,35.00,0.00,4806525910543,,Stock,True,False,0.00,False,False,0.00,26.50,False,5/11/2026 12:00:00 AM";
        CsvProductFile file = Parse(Header + "\n" + row + "\n");

        CsvProductRow parsed = Assert.Single(file.Rows);
        Assert.Equal(17, parsed.Fields.Count);
        Assert.Equal("GILAS PAPER PLATE 9\"", parsed.Fields[0]);
    }

    [Fact]
    public void EmptyCells_StayEmpty_AroundAdjacentCommas()
    {
        // barcode1 and barcode2 are empty in the client profile most rows.
        string row = "a,a,Cat,35.00,39.00,,,Stock,False,False,0.00,False,True,0.00,20.00,False,6/18/2026 12:00:00 AM";
        CsvProductFile file = Parse(Header + "\n" + row + "\n");

        CsvProductRow parsed = Assert.Single(file.Rows);
        Assert.Equal("", parsed.Fields[5]); // barcode1
        Assert.Equal("", parsed.Fields[6]); // barcode2
        Assert.Equal("Stock", parsed.Fields[7]); // Warehouse still lands on its slot
    }

    [Fact]
    public void NegativeAndZeroNumbers_SurviveAsText()
    {
        string row = "a,a,Cat,35.00,0.00,4806515910543,,Stock,True,False,0.00,False,True,-21.00,29.96,False,6/18/2026 12:00:00 AM";
        CsvProductRow parsed = Assert.Single(Parse(Header + "\n" + row + "\n").Rows);

        Assert.Equal("-21.00", parsed.Fields[13]); // stock_qty negative (informational)
        Assert.Equal("0.00", parsed.Fields[10]);   // s_PWD_SC_perc zero
    }

    [Fact]
    public void Fields_AreTrimmed()
    {
        string row = "  a  , a , Cat , 35.00 ,39.00,4806515910543,,Stock,False,False,0.00,False,True,0.00,20.00,False,6/18/2026 12:00:00 AM ";
        CsvProductRow parsed = Assert.Single(Parse(Header + "\n" + row + "\n").Rows);

        Assert.Equal("a", parsed.Fields[0]);
        Assert.Equal("a", parsed.Fields[1]);
        Assert.Equal("Cat", parsed.Fields[2]);
        Assert.Equal("35.00", parsed.Fields[3]);
    }

    private static string ValidRow(string name, string rp) =>
        $"{name},{name},Canned Juices,{rp},39.00,4806515910543,,Stock,False,False,0.00,False,True,0.00,29.96,False,6/18/2026 12:00:00 AM";
}
