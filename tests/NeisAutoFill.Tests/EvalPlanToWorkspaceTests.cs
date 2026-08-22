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
    public void 한_영역에_평가가_여럿이면_여러_줄로_나뉜다()
    {
        // 잘함·보통·노력요함 한 세트 = 평가 하나. 두 세트면 평가가 둘이고 줄도 둘이다.
        var doc = Doc(new EvalArea("한국사", new[]
        {
            Standard("[6사04-01] 선사시대", "선사 추론", "가1", "나1", "다1"),
            Standard("[6사05-02] 조선후기", "서민 문화", "가2", "나2", "다2"),
        }));

        var result = EvalPlanToWorkspace.Convert(doc, ThreeLevels);

        var plan = Assert.Single(result.Plans);
        Assert.Equal(new[] { "한국사", "한국사#2" }, plan.Domains);

        // 갈라 준 것은 <b>이름뿐</b> — 진짜 영역명은 그대로 남아 나이스로 간다
        Assert.All(plan.Domains, d => Assert.Equal("한국사", plan.Criteria[(d, "잘함")].Area));
        Assert.Equal("가1", plan.Criteria[("한국사", "잘함")].Text);
        Assert.Equal("나2", plan.Criteria[("한국사#2", "보통")].Text);
        Assert.Empty(result.Notes);
    }

    /// <summary>구분 꼬리는 <b>속으로만</b> 쓴다 — 떼면 언제나 진짜 영역명이 나온다.</summary>
    [Fact]
    public void 구분_꼬리는_떼면_진짜_영역명이다()
    {
        var doc = Doc(new EvalArea("한국사", new[]
        {
            Standard("기준1", "", "가1", "나1", "다1"),
            Standard("기준2", "", "가2", "나2", "다2"),
        }));

        var plan = Assert.Single(EvalPlanToWorkspace.Convert(doc, ThreeLevels).Plans);

        Assert.Equal(new[] { "한국사", "한국사#2" }, plan.Domains);
        Assert.All(plan.Domains, d => Assert.Equal("한국사", PlanKeys.NameOf(d)));
        Assert.All(plan.Domains, d => Assert.Equal("한국사", plan.Criteria[(d, "잘함")].Area));
    }

    [Fact]
    public void 평가가_하나뿐인_영역은_이름이_그대로다()
    {
        var doc = Doc(new EvalArea("문법", new[] { Standard("기준", "글쓰기", "가", "나", "다") }));

        var plan = Assert.Single(EvalPlanToWorkspace.Convert(doc, ThreeLevels).Plans);

        Assert.Equal(new[] { "문법" }, plan.Domains);   // 갈라 줄 이유가 없으면 건드리지 않는다
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

    /// <summary>
    /// 나이스로 갈 때는 <b>다시 한 영역으로 모인다</b> — 나이스는 영역 아래에 성취기준 여럿을 받는다.
    /// 표에서 갈라 준 이름(<c>한국사 · 선사 추론</c>)이 나이스에 들어가면 안 된다.
    /// </summary>
    [Fact]
    public void 나이스로_올릴_때는_진짜_영역으로_다시_모인다()
    {
        var doc = Doc(new EvalArea("한국사", new[]
        {
            Standard("[6사04-01] 선사시대", "선사 추론", "가1", "나1", "다1"),
            Standard("[6사05-02] 조선후기", "서민 문화", "가2", "나2", "다2"),
        }));
        var plans = EvalPlanToWorkspace.Convert(doc, ThreeLevels).Plans;

        var back = EvalPlanFromWorkspace.Convert(
            plans, ThreeLevels.Levels.Select(l => l.Label).ToList());

        var subject = Assert.Single(back.Subjects);
        var area = Assert.Single(subject.Areas);
        Assert.Equal("한국사", area.Name);
        Assert.Equal(2, area.Standards.Count);
        Assert.Equal("[6사04-01] 선사시대", area.Standards[0].Standard);
        Assert.Equal("서민 문화", area.Standards[1].Element);
        Assert.All(area.Standards, s => Assert.Equal(3, s.LevelCount));
    }
}
