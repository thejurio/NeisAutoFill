using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class NarrativeWorkbookLoaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"narr_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Round_trips_through_writer()
    {
        var data = new Dictionary<string, IReadOnlyList<(string, string, string)>>
        {
            ["국어"] = new List<(string, string, string)>
            {
                ("1", "홍길동", "발표에 적극적으로 참여함."),
                ("2", "김철수", "글의 주제를 정확히 파악함."),
            },
            ["수학"] = new List<(string, string, string)>
            {
                ("1", "홍길동", "분수 나눗셈을 능숙히 해결함."),
            },
        };
        NarrativeWorkbookWriter.Write(_path, data);

        var loaded = NarrativeWorkbookLoader.Load(_path);
        Assert.Equal(2, loaded.Count);
        Assert.Equal(2, loaded["국어"].Count);
        Assert.Equal(("2", "김철수", "글의 주제를 정확히 파악함."), loaded["국어"][1]);
        Assert.Equal(("1", "홍길동", "분수 나눗셈을 능숙히 해결함."), loaded["수학"][0]);
    }

    [Fact]
    public void Skips_rows_without_name_or_text()
    {
        using (var wb = new ClosedXML.Excel.XLWorkbook())
        {
            var ws = wb.AddWorksheet("과학");
            ws.Cell(1, 1).Value = "번호"; ws.Cell(1, 2).Value = "이름"; ws.Cell(1, 3).Value = "서술문";
            ws.Cell(2, 1).Value = "1"; ws.Cell(2, 2).Value = "홍길동"; ws.Cell(2, 3).Value = "관찰을 잘함.";
            ws.Cell(3, 1).Value = "2"; ws.Cell(3, 2).Value = "김철수";   // 서술문 없음
            ws.Cell(4, 2).Value = ""; ws.Cell(4, 3).Value = "이름 없음";  // 이름 없음
            wb.SaveAs(_path);
        }

        var loaded = NarrativeWorkbookLoader.Load(_path);
        var rows = Assert.Single(loaded).Value;
        Assert.Single(rows);
        Assert.Equal("홍길동", rows[0].Name);
    }
}
