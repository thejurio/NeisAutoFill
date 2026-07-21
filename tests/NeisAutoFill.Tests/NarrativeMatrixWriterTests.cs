using ClosedXML.Excel;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class NarrativeMatrixWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"matrix_out_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static (string, string, IReadOnlyDictionary<string, string>) Row(
        string no, string name, params (string Subject, string Text)[] cells) =>
        (no, name, cells.ToDictionary(c => c.Subject, c => c.Text));

    [Fact]
    public void Writes_students_as_rows_and_subjects_as_columns()
    {
        var subjects = new[] { "국어", "수학", "영어" };
        var students = new[]
        {
            Row("1", "강나연", ("국어", "글의 구조를 파악함"), ("수학", "규칙을 찾아 설명함"), ("영어", "대화를 이해함")),
            Row("2", "고하영", ("국어", "주제를 정리함"), ("영어", "세부 내용을 파악함")),   // 수학은 빈칸
        };

        NarrativeMatrixWriter.Write(_path, subjects, students);

        using var wb = new XLWorkbook(_path);
        var ws = wb.Worksheet(NarrativeMatrixWriter.SheetName);

        // 헤더: 번호 | 이름 | 국어 | 수학 | 영어
        Assert.Equal("번호", ws.Cell(1, 1).GetString());
        Assert.Equal("이름", ws.Cell(1, 2).GetString());
        Assert.Equal("국어", ws.Cell(1, 3).GetString());
        Assert.Equal("수학", ws.Cell(1, 4).GetString());
        Assert.Equal("영어", ws.Cell(1, 5).GetString());

        // 1행 학생: 전 과목 채워짐
        Assert.Equal("1", ws.Cell(2, 1).GetString());
        Assert.Equal("강나연", ws.Cell(2, 2).GetString());
        Assert.Equal("글의 구조를 파악함", ws.Cell(2, 3).GetString());
        Assert.Equal("규칙을 찾아 설명함", ws.Cell(2, 4).GetString());

        // 2행 학생: 수학 칸은 빈칸으로 남음
        Assert.Equal("고하영", ws.Cell(3, 2).GetString());
        Assert.Equal("주제를 정리함", ws.Cell(3, 3).GetString());
        Assert.Equal("", ws.Cell(3, 4).GetString());
        Assert.Equal("세부 내용을 파악함", ws.Cell(3, 5).GetString());
    }

    [Fact]
    public void Empty_input_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NarrativeMatrixWriter.Write(_path, Array.Empty<string>(),
                Array.Empty<(string, string, IReadOnlyDictionary<string, string>)>()));
    }
}
