using ClosedXML.Excel;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class WorkbookLoaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"grades_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private void Write(Action<IXLWorksheet> build, string sheetName = "국어")
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheetName);
        build(ws);
        wb.SaveAs(_path);
    }

    [Fact]
    public void Parses_areas_and_students()
    {
        Write(ws =>
        {
            ws.Cell(1, 1).Value = "번호"; ws.Cell(1, 2).Value = "이름";
            ws.Cell(1, 3).Value = "문법"; ws.Cell(1, 4).Value = "읽기";
            ws.Cell(1, 5).Value = "과목 특기사항";
            ws.Cell(2, 1).Value = 1; ws.Cell(2, 2).Value = "김다예";
            ws.Cell(2, 3).Value = "잘함"; ws.Cell(2, 4).Value = "보통";
            ws.Cell(2, 5).Value = "성실함";
        });

        var sheets = WorkbookLoader.Load(_path);
        var kor = Assert.Single(sheets);

        Assert.Equal("국어", kor.SubjectName);
        Assert.Equal(new[] { "문법", "읽기" }, kor.Areas);         // 특기사항 제외
        var st = Assert.Single(kor.Students);
        Assert.Equal("김다예", st.Name);
        Assert.Equal("잘함", st.Grades["문법"]);
        Assert.Equal("성실함", st.SpecialNote);
    }

    [Fact]
    public void Skips_sheet_without_number_name_headers()
    {
        Write(ws =>
        {
            ws.Cell(1, 1).Value = "영역"; ws.Cell(1, 2).Value = "성취기준";
            ws.Cell(2, 1).Value = "문법";
        }, "평가기준");

        Assert.Empty(WorkbookLoader.Load(_path));
    }

    [Fact]
    public void Empty_area_cell_is_omitted_from_grades()
    {
        Write(ws =>
        {
            ws.Cell(1, 1).Value = "번호"; ws.Cell(1, 2).Value = "이름";
            ws.Cell(1, 3).Value = "문법"; ws.Cell(1, 4).Value = "읽기";
            ws.Cell(2, 1).Value = 1; ws.Cell(2, 2).Value = "김다예";
            ws.Cell(2, 3).Value = "잘함";      // 읽기 공란
        });

        var st = WorkbookLoader.Load(_path)[0].Students[0];
        Assert.True(st.Grades.ContainsKey("문법"));
        Assert.False(st.Grades.ContainsKey("읽기"));
    }

    [Fact]
    public void Row_without_name_is_skipped()
    {
        Write(ws =>
        {
            ws.Cell(1, 1).Value = "번호"; ws.Cell(1, 2).Value = "이름"; ws.Cell(1, 3).Value = "문법";
            ws.Cell(2, 1).Value = 1; ws.Cell(2, 2).Value = "김다예"; ws.Cell(2, 3).Value = "잘함";
            ws.Cell(3, 1).Value = 2; /* 이름 없음 */ ws.Cell(3, 3).Value = "보통";
        });

        Assert.Single(WorkbookLoader.Load(_path)[0].Students);
    }
}
