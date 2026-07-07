using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class TemplateWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tpl_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Step1_template_roundtrips_through_plan_loader()
    {
        TemplateWriter.WriteStep1Template(_path, GradePresets.ThreeLevel);

        // 국어 시트만 예시가 있어 감지되고, 나머지 빈 과목 시트는 무시돼야 함
        var plans = PlanWorkbookLoader.Load(_path, GradePresets.ThreeLevel);
        var kor = Assert.Single(plans);
        Assert.Equal("국어", kor.SubjectName);
        Assert.Equal(3, kor.Criteria.Count);   // 척도 단계 수만큼 예시 행

        var roster = PlanWorkbookLoader.LoadRoster(_path);
        Assert.Equal(3, roster.Count);         // 샘플 명단 3명
        Assert.Equal("홍길동", roster[0].Name);
    }

    [Fact]
    public void Step1_sample_rows_follow_active_scale()
    {
        TemplateWriter.WriteStep1Template(_path, GradePresets.FiveLevel);
        var plans = PlanWorkbookLoader.Load(_path, GradePresets.FiveLevel);
        Assert.Equal(5, Assert.Single(plans).Criteria.Count);
    }

    [Fact]
    public void Step2_template_roundtrips_through_workbook_loader()
    {
        var plans = new[]
        {
            new SubjectPlan("국어", new[] { "문법", "읽기" },
                new Dictionary<(string, string), CriteriaEntry>()),
            new SubjectPlan("수학", new[] { "수와 연산" },
                new Dictionary<(string, string), CriteriaEntry>()),
        };
        var roster = new List<(string, string)> { ("1", "김다예"), ("2", "박서연") };

        TemplateWriter.WriteStep2Template(_path, plans, roster);

        var sheets = WorkbookLoader.Load(_path);
        Assert.Equal(2, sheets.Count);
        var kor = sheets.First(s => s.SubjectName == "국어");
        Assert.Equal(new[] { "문법", "읽기" }, kor.Areas);      // 특기사항 컬럼 제외 확인
        Assert.Equal(2, kor.Students.Count);
        Assert.Equal("김다예", kor.Students[0].Name);
        Assert.Empty(kor.Students[0].Grades);                    // 성적 공란
    }

    [Fact]
    public void Step2_without_plan_throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TemplateWriter.WriteStep2Template(_path,
                Array.Empty<SubjectPlan>(), new List<(string, string)> { ("1", "김") }));
    }
}
