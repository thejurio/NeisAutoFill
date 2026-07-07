using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

public class EvaluationAssemblerTests
{
    private static readonly SubjectPlan Plan = new("국어",
        new[] { "문법", "읽기" },
        new Dictionary<(string, string), CriteriaEntry>
        {
            [("문법", "잘함")] = new("문법 요소를 정확히 사용한다.", "[6국04-01]"),
            [("읽기", "보통")] = new("글의 중심 내용을 파악한다.", null),
        });

    [Fact]
    public void Combines_grades_with_plan_criteria()
    {
        var st = new Student("1", "김다예", new Dictionary<string, string>
        { ["문법"] = "잘함", ["읽기"] = "보통" }, "발표에 적극적임");

        var pts = EvaluationAssembler.BuildDomainPoints(st, new[] { "문법", "읽기" }, Plan, GradePresets.ThreeLevel);

        Assert.Equal(2, pts.Count);
        Assert.Equal("문법 요소를 정확히 사용한다.", pts[0].CriteriaText);
        Assert.Equal("[6국04-01]", pts[0].Achievement);
        Assert.Null(pts[1].Achievement);
    }

    [Fact]
    public void Missing_criteria_yields_placeholder_not_exclusion()
    {
        // 등급은 유효하지만 계획서에 그 (영역,등급) 기준이 없음 → "[기준 없음]" (DooEval 동일)
        var st = new Student("1", "김다예", new Dictionary<string, string> { ["문법"] = "보통" });
        var pts = EvaluationAssembler.BuildDomainPoints(st, new[] { "문법" }, Plan, GradePresets.ThreeLevel);

        var p = Assert.Single(pts);
        Assert.Equal(EvaluationAssembler.NoCriteriaText, p.CriteriaText);
    }

    [Fact]
    public void Null_plan_still_builds_points_with_placeholder()
    {
        var st = new Student("1", "김다예", new Dictionary<string, string> { ["문법"] = "잘함" });
        var pts = EvaluationAssembler.BuildDomainPoints(st, new[] { "문법" }, null, GradePresets.ThreeLevel);
        Assert.Equal(EvaluationAssembler.NoCriteriaText, Assert.Single(pts).CriteriaText);
    }

    [Fact]
    public void Grades_outside_scale_are_excluded()
    {
        var st = new Student("1", "김다예", new Dictionary<string, string>
        { ["문법"] = "상", ["읽기"] = "보통" });

        var pts = EvaluationAssembler.BuildDomainPoints(st, new[] { "문법", "읽기" }, Plan, GradePresets.ThreeLevel);

        var p = Assert.Single(pts);       // '상'은 3단계 척도 밖 → 제외
        Assert.Equal("읽기", p.DomainName);
    }
}
