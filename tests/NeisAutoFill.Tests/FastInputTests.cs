using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>
/// <b>빠른 입력</b> — 대조 없이 화면 순서 그대로 넣는 방식 (설정, 사용자 요청 2026-08-22).
/// 여기가 틀리면 <b>엉뚱한 학생에게 성적이 들어간다</b>. 그래서 어긋나면 멈추는지까지 확인한다.
/// </summary>
public class FastInputTests
{
    private static readonly GradeScale Scale = GradePresets.ThreeLevel;
    private static readonly string[] Areas = { "듣기·말하기", "읽기" };

    private static Student S(string no, string name, string a1, string a2) =>
        new(no, name, new Dictionary<string, string> { [Areas[0]] = a1, [Areas[1]] = a2 });

    /// <summary>학생 2명 × 영역 2개 = 화면 4줄 (나이스가 주는 순서 그대로).</summary>
    private static Dictionary<int, RowMeta> Screen(params (string No, string Name, string Area)[] rows) =>
        rows.Select((r, i) => (i, r)).ToDictionary(x => x.i, x => new RowMeta(x.r.No, x.r.Name, x.r.Area));

    [Fact]
    public void 화면_순서대로_배정한다()
    {
        var screen = Screen(
            ("1", "홍길동", "듣기·말하기"), ("1", "홍길동", "읽기"),
            ("2", "김철수", "듣기·말하기"), ("2", "김철수", "읽기"));

        var result = StudentMatcher.Build(screen,
            new[] { S("1", "홍길동", "잘함", "보통"), S("2", "김철수", "보통", "노력요함") },
            Scale, Areas, StudentMatcher.MatchMode.ByRowOrder);

        Assert.Null(result.FatalError);
        Assert.Equal(new[] { "잘함", "보통", "보통", "노력요함" },
            result.Todo.OrderBy(t => t.RowIndex).Select(t => t.TargetGrade));
    }

    /// <summary>
    /// <b>이름이 달라도 순서대로 넣는다</b> — 그것이 빠른 입력의 뜻이다.
    /// (정확한 입력이었다면 여기서 확인 창이 떴을 것이다.)
    /// </summary>
    [Fact]
    public void 이름이_달라도_순서를_따른다()
    {
        var screen = Screen(
            ("1", "홍길동", "듣기·말하기"), ("1", "홍길동", "읽기"),
            ("2", "리철수", "듣기·말하기"), ("2", "리철수", "읽기"));   // 명단은 '김철수'

        var result = StudentMatcher.Build(screen,
            new[] { S("1", "홍길동", "잘함", "보통"), S("2", "김철수", "보통", "노력요함") },
            Scale, Areas, StudentMatcher.MatchMode.ByRowOrder);

        Assert.Null(result.FatalError);
        Assert.Equal(4, result.Todo.Count);
        Assert.Equal("보통", result.Todo.Single(t => t.RowIndex == 2).TargetGrade);
    }

    [Fact]
    public void 화면_줄이_모자라면_아무것도_넣지_않고_멈춘다()
    {
        // 한 학생이 화면에서 빠졌다 — 그대로 순서로 넣으면 그 뒤가 통째로 밀린다
        var screen = Screen(
            ("1", "홍길동", "듣기·말하기"), ("1", "홍길동", "읽기"),
            ("2", "김철수", "듣기·말하기"));

        var result = StudentMatcher.Build(screen,
            new[] { S("1", "홍길동", "잘함", "보통"), S("2", "김철수", "보통", "노력요함") },
            Scale, Areas, StudentMatcher.MatchMode.ByRowOrder);

        Assert.NotNull(result.FatalError);
        Assert.Empty(result.Todo);
        Assert.Contains("3줄", result.FatalError);
    }

    [Fact]
    public void 화면_줄이_더_많아도_멈춘다()
    {
        var screen = Screen(
            ("1", "홍길동", "듣기·말하기"), ("1", "홍길동", "읽기"),
            ("2", "김철수", "듣기·말하기"), ("2", "김철수", "읽기"),
            ("3", "이영희", "듣기·말하기"), ("3", "이영희", "읽기"));

        var result = StudentMatcher.Build(screen,
            new[] { S("1", "홍길동", "잘함", "보통"), S("2", "김철수", "보통", "노력요함") },
            Scale, Areas, StudentMatcher.MatchMode.ByRowOrder);

        Assert.NotNull(result.FatalError);
        Assert.Empty(result.Todo);
    }

    // ── 서술문 ─────────────────────────────

    private static Dictionary<int, (string? No, string? Name)> Rows(params (string No, string Name)[] rows) =>
        rows.Select((r, i) => (i, r)).ToDictionary(x => x.i, x => ((string?)x.r.No, (string?)x.r.Name));

    [Fact]
    public void 서술문도_화면_순서대로_배정한다()
    {
        var result = NarrativeMatcher.BuildByOrder(
            Rows(("1", "홍길동"), ("2", "김철수")),
            new[] { new NarrativeEntry("1", "홍길동", "가"), new NarrativeEntry("2", "김철수", "나") });

        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { "가", "나" }, result.Todo.OrderBy(t => t.RowIndex).Select(t => t.Entry.Text));
    }

    [Fact]
    public void 서술문도_수가_다르면_아무것도_넣지_않는다()
    {
        var result = NarrativeMatcher.BuildByOrder(
            Rows(("1", "홍길동"), ("2", "김철수"), ("3", "이영희")),
            new[] { new NarrativeEntry("1", "홍길동", "가"), new NarrativeEntry("2", "김철수", "나") });

        Assert.Empty(result.Todo);
        Assert.Contains("멈췄습니다", Assert.Single(result.Skipped).Reason);
    }
}
