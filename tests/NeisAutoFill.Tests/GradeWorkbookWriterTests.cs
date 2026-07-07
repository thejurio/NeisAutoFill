using NeisAutoFill.Core.Models;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class GradeWorkbookWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"grades_out_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Written_grades_roundtrip_through_loader()
    {
        var sheets = new[]
        {
            new SubjectSheet("국어", new[] { "문법", "읽기" }, new[]
            {
                new Student("1", "김다예", new Dictionary<string, string> { ["문법"] = "잘함", ["읽기"] = "보통" }, "발표에 적극적임"),
                new Student("2", "박서연", new Dictionary<string, string> { ["문법"] = "노력요함" }, null),
            }),
        };

        GradeWorkbookWriter.Write(_path, sheets);
        var reloaded = WorkbookLoader.Load(_path);

        var kor = Assert.Single(reloaded);
        Assert.Equal(new[] { "문법", "읽기" }, kor.Areas);   // 특기사항 컬럼 제외
        Assert.Equal(2, kor.Students.Count);

        var s1 = kor.Students[0];
        Assert.Equal("김다예", s1.Name);
        Assert.Equal("잘함", s1.Grades["문법"]);
        Assert.Equal("보통", s1.Grades["읽기"]);
        Assert.Equal("발표에 적극적임", s1.SpecialNote);

        var s2 = kor.Students[1];
        Assert.Equal("노력요함", s2.Grades["문법"]);
        Assert.False(s2.Grades.ContainsKey("읽기"));   // 빈 등급은 저장 안 됨
        Assert.Null(s2.SpecialNote);
    }
}
