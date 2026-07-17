using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

public class ClipboardTableTests
{
    [Fact]
    public void Parses_excel_tsv_with_trailing_newline()
    {
        var rows = ClipboardTable.Parse("1\t홍길동\r\n2\t김철수\r\n");
        Assert.Equal(2, rows.Length);
        Assert.Equal(new[] { "1", "홍길동" }, rows[0]);
        Assert.Equal(new[] { "2", "김철수" }, rows[1]);
    }

    [Fact]
    public void Keeps_interior_empty_rows_and_trims_cells()
    {
        var rows = ClipboardTable.Parse("a\t b \n\nc\n");
        Assert.Equal(3, rows.Length);
        Assert.Equal("b", rows[0][1]);
        Assert.Equal(new[] { "" }, rows[1]);
    }

    [Fact]
    public void Empty_input_gives_empty_table()
    {
        Assert.Empty(ClipboardTable.Parse(null));
        Assert.Empty(ClipboardTable.Parse(""));
        Assert.Empty(ClipboardTable.Parse("\r\n"));
    }

    [Fact]
    public void Roster_reads_number_and_name_columns()
    {
        var roster = ClipboardTable.ToRoster(ClipboardTable.Parse("1\t홍길동\n2\t김철수\n"));
        Assert.Equal(new[] { ("1", "홍길동"), ("2", "김철수") }, roster);
    }

    [Fact]
    public void Roster_skips_header_and_autonumbers_name_only_lists()
    {
        var roster = ClipboardTable.ToRoster(ClipboardTable.Parse("번호\t이름\n홍길동\n김철수\n"));
        Assert.Equal(new[] { ("1", "홍길동"), ("2", "김철수") }, roster);
    }

    [Fact]
    public void Roster_ignores_blank_rows()
    {
        var roster = ClipboardTable.ToRoster(ClipboardTable.Parse("1\t홍길동\n\n3\t이영희\n"));
        Assert.Equal(2, roster.Count);
    }
}
