using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class PlanWorkbookWriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"planw_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static SubjectPlan SamplePlan() => new(
        "국어",
        new[] { "듣기·말하기", "읽기" },
        new Dictionary<(string, string), CriteriaEntry>
        {
            [("듣기·말하기", "잘함")] = new("매체를 활용해 발표한다.", "[6국01-05]"),
            [("듣기·말하기", "보통")] = new("내용을 구성해 발표한다.", "[6국01-05]"),
            [("듣기·말하기", "노력요함")] = new("제한적으로 발표한다.", "[6국01-05]"),
            [("읽기", "잘함")] = new("주제를 파악하며 읽는다.", "[6국02-01]"),
            [("읽기", "보통")] = new("내용을 파악하며 읽는다.", "[6국02-01]"),
            [("읽기", "노력요함")] = new("도움을 받아 읽는다.", "[6국02-01]"),
        });

    /// <summary>평가요소는 나이스가 성취기준과 따로 요구하는 칸이라 파일에도 남아야 한다.</summary>
    [Fact]
    public void 평가요소도_왕복한다()
    {
        var scale = GradePresets.ThreeLevel;
        var plan = new SubjectPlan("국어", new[] { "읽기" },
            new Dictionary<(string, string), CriteriaEntry>
            {
                [("읽기", "잘함")] = new("주제를 파악하며 읽는다.", "[6국02-01]", "책 내용 간추리기"),
                [("읽기", "보통")] = new("내용을 파악하며 읽는다.", "[6국02-01]", "책 내용 간추리기"),
            });

        PlanWorkbookWriter.Write(_path, new[] { plan }, new List<(string, string)> { ("1", "홍길동") }, scale);
        var loaded = Assert.Single(PlanWorkbookLoader.Load(_path, scale));

        Assert.Equal("책 내용 간추리기", loaded.Criteria[("읽기", "잘함")].Element);
        Assert.Equal("책 내용 간추리기", loaded.Criteria[("읽기", "보통")].Element);
    }

    /// <summary>평가요소 칸이 없던 시절 파일도 그대로 열려야 한다 (없으면 null).</summary>
    [Fact]
    public void 평가요소가_없어도_읽힌다()
    {
        var scale = GradePresets.ThreeLevel;

        PlanWorkbookWriter.Write(_path, new[] { SamplePlan() }, new List<(string, string)> { ("1", "홍") }, scale);
        var loaded = Assert.Single(PlanWorkbookLoader.Load(_path, scale));

        Assert.Null(loaded.Criteria[("읽기", "보통")].Element);
        Assert.Equal("[6국02-01]", loaded.Criteria[("읽기", "보통")].Achievement);
    }

    /// <summary>한 영역에 평가가 둘이면 파일을 거쳐도 둘로 남아야 한다 (사용자 확인 2026-08-22).</summary>
    [Fact]
    public void 한_영역의_평가_둘이_왕복해도_둘로_남는다()
    {
        var scale = GradePresets.ThreeLevel;
        var plan = new SubjectPlan("사회",
            new[] { "한국사 · 선사 추론", "한국사 · 서민 문화" },
            new Dictionary<(string, string), CriteriaEntry>
            {
                [("한국사 · 선사 추론", "잘함")] = new("가1", "[6사04-01]", "선사 추론", "한국사"),
                [("한국사 · 선사 추론", "보통")] = new("나1", "[6사04-01]", "선사 추론", "한국사"),
                [("한국사 · 서민 문화", "잘함")] = new("가2", "[6사05-02]", "서민 문화", "한국사"),
                [("한국사 · 서민 문화", "보통")] = new("나2", "[6사05-02]", "서민 문화", "한국사"),
            });

        PlanWorkbookWriter.Write(_path, new[] { plan }, new List<(string, string)> { ("1", "홍") }, scale);
        var loaded = Assert.Single(PlanWorkbookLoader.Load(_path, scale));

        Assert.Equal(2, loaded.Domains.Count);
        Assert.All(loaded.Domains, d => Assert.Equal("한국사", loaded.Criteria[(d, "잘함")].Area));
        Assert.Equal(new[] { "[6사04-01]", "[6사05-02]" },
            loaded.Domains.Select(d => loaded.Criteria[(d, "잘함")].Achievement));
        Assert.Equal(new[] { "가1", "가2" }, loaded.Domains.Select(d => loaded.Criteria[(d, "잘함")].Text));
    }

    [Fact]
    public void Round_trips_through_loader()
    {
        var scale = GradePresets.ThreeLevel;
        var roster = new List<(string, string)> { ("1", "홍길동"), ("2", "김철수") };

        PlanWorkbookWriter.Write(_path, new[] { SamplePlan() }, roster, scale);

        var plans = PlanWorkbookLoader.Load(_path, scale);
        var kor = Assert.Single(plans);
        Assert.Equal("국어", kor.SubjectName);
        Assert.Equal(new[] { "듣기·말하기", "읽기" }, kor.Domains);
        Assert.Equal(6, kor.Criteria.Count);
        Assert.Equal("내용을 파악하며 읽는다.", kor.Criteria[("읽기", "보통")].Text);
        Assert.Equal("[6국02-01]", kor.Criteria[("읽기", "보통")].Achievement);

        Assert.Equal(roster, PlanWorkbookLoader.LoadRoster(_path));
        Assert.True(PlanWorkbookLoader.LooksLikePlan(_path));
    }

    [Fact]
    public void Writes_empty_criteria_cells_without_failing()
    {
        var scale = GradePresets.ThreeLevel;
        var plan = new SubjectPlan("수학", new[] { "수와 연산" },
            new Dictionary<(string, string), CriteriaEntry>
            {
                [("수와 연산", "잘함")] = new("능숙히 해결한다.", null),
            });

        PlanWorkbookWriter.Write(_path, new[] { plan }, new List<(string, string)> { ("1", "가나다") }, scale);

        var loaded = Assert.Single(PlanWorkbookLoader.Load(_path, scale));
        Assert.Equal("능숙히 해결한다.", loaded.Criteria[("수와 연산", "잘함")].Text);
        Assert.False(loaded.Criteria.ContainsKey(("수와 연산", "보통")));   // 빈 기준은 저장 안 됨
    }
}
