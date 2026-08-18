using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 매핑 우선순위 해석 (기술설계 §5, D-007, 로드맵 T1).
/// 핵심: 가장 구체적인 규칙이 이기고, 같은 우선순위에서 갈리면 <b>임의 선택 금지</b>.
/// </summary>
public class TimetableMappingResolverTests
{
    private const string 담임 = "L|체육|교사a|account-a";
    private const string 보건 = "L|체육|교사b|account-b";
    private const string 진로 = "C|Career|진로활동|교사c|account-c";

    private static TimetableCell 월요일3교시 => new(new DateOnly(2026, 8, 24), 3);   // 2026-08-24 = 월
    private static TimetableCell 수요일3교시 => new(new DateOnly(2026, 8, 26), 3);   // 수

    [Fact]
    public void 맞는_규칙이_없으면_미해결이다()
    {
        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, Array.Empty<TimetableMappingRule>());

        Assert.Equal(MappingResolutionKind.Unresolved, r.Kind);
        Assert.Equal("", r.TargetStableKey);   // null 이나 첫 후보로 숨기지 않는다
    }

    [Fact]
    public void 기본값만_있으면_기본값을_쓴다()
    {
        var rules = new[] { new TimetableMappingRule("체", 담임, MappingScope.Default) };

        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, rules);

        Assert.Equal(MappingResolutionKind.Resolved, r.Kind);
        Assert.Equal(담임, r.TargetStableKey);
    }

    [Fact]
    public void 요일_규칙이_기본값을_이긴다()
    {
        var rules = new[]
        {
            new TimetableMappingRule("체", 담임, MappingScope.Default),
            new TimetableMappingRule("체", 보건, MappingScope.ForDay(DayOfWeek.Wednesday)),
        };

        Assert.Equal(담임, TimetableMappingResolver.Resolve("체", 월요일3교시, rules).TargetStableKey);
        Assert.Equal(보건, TimetableMappingResolver.Resolve("체", 수요일3교시, rules).TargetStableKey);
    }

    [Fact]
    public void 요일_교시_규칙이_요일_규칙을_이긴다()
    {
        var rules = new[]
        {
            new TimetableMappingRule("체", 담임, MappingScope.ForDay(DayOfWeek.Monday)),
            new TimetableMappingRule("체", 보건, MappingScope.ForDayPeriod(DayOfWeek.Monday, 3)),
        };

        Assert.Equal(보건, TimetableMappingResolver.Resolve("체", 월요일3교시, rules).TargetStableKey);
        Assert.Equal(담임, TimetableMappingResolver.Resolve("체", new TimetableCell(new DateOnly(2026, 8, 24), 5), rules).TargetStableKey);
    }

    [Fact]
    public void 특정_날짜_예외가_가장_강하다()
    {
        var rules = new[]
        {
            new TimetableMappingRule("체", 담임, MappingScope.Default),
            new TimetableMappingRule("체", 보건, MappingScope.ForDay(DayOfWeek.Monday)),
            new TimetableMappingRule("체", 진로, MappingScope.ForDate(new DateOnly(2026, 8, 24), 3)),
        };

        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, rules);

        Assert.Equal(진로, r.TargetStableKey);
        Assert.Equal(MappingScopeKind.SpecificDate, r.AppliedRule!.Scope.Kind);
    }

    [Fact]
    public void 같은_우선순위에_서로_다른_대상이면_충돌이다()
    {
        var rules = new[]
        {
            new TimetableMappingRule("체", 담임, MappingScope.ForDay(DayOfWeek.Monday)),
            new TimetableMappingRule("체", 보건, MappingScope.ForDay(DayOfWeek.Monday)),
        };

        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, rules);

        Assert.Equal(MappingResolutionKind.Conflict, r.Kind);
        Assert.Equal(2, r.ConflictingRules!.Count);
        Assert.Equal("", r.TargetStableKey);   // 첫 항목을 고르지 않는다
    }

    [Fact]
    public void 대상이_같은_중복_규칙은_충돌이_아니다()
    {
        // 모호하지 않으므로 막을 이유가 없다 — "첫 항목을 고르는 것"과 다르다
        var rules = new[]
        {
            new TimetableMappingRule("체", 담임, MappingScope.ForDay(DayOfWeek.Monday)),
            new TimetableMappingRule("체", 담임, MappingScope.ForDay(DayOfWeek.Monday)),
        };

        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, rules);

        Assert.Equal(MappingResolutionKind.Resolved, r.Kind);
        Assert.Equal(담임, r.TargetStableKey);
    }

    [Fact]
    public void 입력_안_함은_미해결과_다른_상태다()
    {
        var rules = new[] { TimetableMappingRule.Skip("체", MappingScope.ForDay(DayOfWeek.Monday)) };

        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, rules);

        Assert.Equal(MappingResolutionKind.Skip, r.Kind);
        Assert.NotEqual(MappingResolutionKind.Unresolved, r.Kind);
    }

    [Fact]
    public void 다른_원본_표기의_규칙은_적용되지_않는다()
    {
        var rules = new[] { new TimetableMappingRule("국", 담임, MappingScope.Default) };

        Assert.Equal(MappingResolutionKind.Unresolved,
            TimetableMappingResolver.Resolve("체", 월요일3교시, rules).Kind);
    }

    [Fact]
    public void 원본_표기_비교는_공백_괄호_차이를_흡수한다()
    {
        var rules = new[] { new TimetableMappingRule("창", 진로, MappingScope.Default) };

        Assert.Equal(진로, TimetableMappingResolver.Resolve(" 창 ", 월요일3교시, rules).TargetStableKey);
    }

    [Fact]
    public void 선택_이유를_항상_설명할_수_있다()
    {
        var rules = new[]
        {
            new TimetableMappingRule("체", 보건, MappingScope.ForDayPeriod(DayOfWeek.Monday, 3)),
        };

        var r = TimetableMappingResolver.Resolve("체", 월요일3교시, rules);

        Assert.Contains("월요일 3교시", r.Describe());
    }
}
