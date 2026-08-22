using NeisAutoFill.Core.Evaluation;
using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>문서에서 읽은 계획 → [평가계획] 표 옮기기 (EvalPlanToWorkspace).</summary>
public class EvalPlanToWorkspaceTests
{
    private static readonly GradeScale ThreeLevels = new("3단계", new[]
    {
        new GradeLevel("잘함"), new GradeLevel("보통"), new GradeLevel("노력요함"),
    });

    private static EvalStandard Standard(string text, string element, params string[] results) =>
        new(text, element, results
            .Select((r, i) => new EvalCriterion(ThreeLevels.Levels[i].Label, r))
            .ToList());

    private static EvalPlanDocument Doc(params EvalArea[] areas) =>
        new(new[] { new EvalSubjectPlan("국어", areas) });

    [Fact]
    public void 영역_하나에_성취기준_하나면_그대로_옮긴다()
    {
        var doc = Doc(new EvalArea("문법", new[] { Standard("[6국04-05] 시간 표현", "글쓰기", "잘한다", "보통이다", "노력") }));

        var result = EvalPlanToWorkspace.Convert(doc, ThreeLevels);

        var plan = Assert.Single(result.Plans);
        Assert.Equal("국어", plan.SubjectName);
        Assert.Equal(new[] { "문법" }, plan.Domains);
        Assert.Equal("잘한다", plan.Criteria[("문법", "잘함")].Text);
        Assert.Equal("[6국04-05] 시간 표현", plan.Criteria[("문법", "잘함")].Achievement);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void 평가요소도_함께_옮긴다()
    {
        var doc = Doc(new EvalArea("문법", new[] { Standard("기준", "시간 표현 사용해 글쓰기", "가", "나", "다") }));

        var plan = Assert.Single(EvalPlanToWorkspace.Convert(doc, ThreeLevels).Plans);

        Assert.Equal("시간 표현 사용해 글쓰기", plan.Criteria[("문법", "보통")].Element);
    }

    [Fact]
    public void 한_영역에_성취기준이_여럿이면_합치고_알린다()
    {
        var doc = Doc(new EvalArea("한국사", new[]
        {
            Standard("[6사04-01] 선사시대", "선사 추론", "가1", "나1", "다1"),
            Standard("[6사05-02] 조선후기", "서민 문화", "가2", "나2", "다2"),
        }));

        var result = EvalPlanToWorkspace.Convert(doc, ThreeLevels);

        var plan = Assert.Single(result.Plans);
        Assert.Equal(new[] { "한국사" }, plan.Domains);   // 줄은 하나로 남는다
        Assert.Equal("가1\n가2", plan.Criteria[("한국사", "잘함")].Text);
        Assert.Equal("[6사04-01] 선사시대\n[6사05-02] 조선후기", plan.Criteria[("한국사", "잘함")].Achievement);
        Assert.Equal("선사 추론\n서민 문화", plan.Criteria[("한국사", "잘함")].Element);

        // 버리지 않았다는 사실을 사용자에게 알린다
        var note = Assert.Single(result.Notes);
        Assert.Contains("성취기준이 2개", note.Text);
    }

    [Fact]
    public void 단계_이름이_달라도_순서로_맞춘다()
    {
        var scale = new GradeScale("3단계", new[] { new GradeLevel("상"), new GradeLevel("중"), new GradeLevel("하") });
        var doc = Doc(new EvalArea("문법", new[] { Standard("기준", "요소", "가", "나", "다") }));

        var plan = Assert.Single(EvalPlanToWorkspace.Convert(doc, scale).Plans);

        Assert.Equal("가", plan.Criteria[("문법", "상")].Text);
        Assert.Equal("다", plan.Criteria[("문법", "하")].Text);
    }

    [Fact]
    public void 척도보다_단계가_많으면_겹치는_만큼만_옮기고_알린다()
    {
        var two = new GradeScale("2단계", new[] { new GradeLevel("잘함"), new GradeLevel("보통") });
        var doc = Doc(new EvalArea("문법", new[] { Standard("기준", "요소", "가", "나", "다") }));

        var result = EvalPlanToWorkspace.Convert(doc, two);

        var plan = Assert.Single(result.Plans);
        Assert.Equal(2, plan.Criteria.Count);
        Assert.Contains(result.Notes, n => n.Text.Contains("평가단계가 3개"));
    }

    [Fact]
    public void 평가기준이_없는_영역은_빼고_교과도_비면_통째로_뺀다()
    {
        var doc = Doc(new EvalArea("빈영역", new[] { new EvalStandard("기준", "요소", Array.Empty<EvalCriterion>()) }));

        Assert.Empty(EvalPlanToWorkspace.Convert(doc, ThreeLevels).Plans);
    }
}
