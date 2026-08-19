using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 교사 배정 세 겹 (시간표 탭).
///
/// <list type="number">
/// <item><b>기본</b> — 과목마다 담당 교사 한 명</item>
/// <item><b>정기 예외</b> — "월요일 3교시 국어만 B선생님" (매주)</item>
/// <item><b>비정기 예외</b> — "9월 15일 3교시만 C선생님" (그 날만)</item>
/// </list>
/// 더 구체적인 규칙이 이긴다. 이게 어긋나면 엉뚱한 교사가 들어간다.
/// </summary>
public class TimetableTeacherAssignmentTests
{
    private static readonly DateOnly 월 = new(2026, 9, 7);    // 월요일
    private static readonly DateOnly 다음월 = new(2026, 9, 14);

    private static readonly string[] 메뉴 =
    {
        "국어(교사A(a))",
        "국어(교사B(b))",
        "국어(교사C(c))",
        "수학(교사A(a))",
        "취소",
    };

    private static readonly TimetableCatalog 카탈로그 = new(TimetableMenuParser.ParseAll(메뉴));

    private static string 키(string menu) => TimetableMenuParser.Parse(menu).StableKey;

    private static string 교사(TimetableCell cell, IEnumerable<TimetableMappingRule> rules, string token = "국")
    {
        var r = TimetableMappingResolver.Resolve(token, cell, rules.ToList());
        if (r.Kind == MappingResolutionKind.Skip) return "입력 안 함";
        if (r.Kind != MappingResolutionKind.Resolved) return r.Kind.ToString();
        return 카탈로그.Find(r.TargetStableKey)?.TeacherName ?? "?";
    }

    private static TimetableMappingRule 기본(string menu) =>
        new("국", 키(menu), MappingScope.Default, true);

    [Fact]
    public void 기본_교사가_모든_칸에_적용된다()
    {
        var rules = new[] { 기본("국어(교사A(a))") };

        Assert.Equal("교사A", 교사(new TimetableCell(월, 1), rules));
        Assert.Equal("교사A", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal("교사A", 교사(new TimetableCell(다음월, 3), rules));
    }

    [Fact]
    public void 정기_예외는_매주_같은_요일_교시에_적용된다()
    {
        // "월요일 3교시 국어만 B선생님, 나머지 국어는 A선생님"
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            new TimetableMappingRule("국", 키("국어(교사B(b))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 3), true),
        };

        Assert.Equal("교사B", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal("교사B", 교사(new TimetableCell(다음월, 3), rules));   // 다음 주도
        Assert.Equal("교사A", 교사(new TimetableCell(월, 1), rules));       // 다른 교시는 기본
        Assert.Equal("교사A", 교사(new TimetableCell(월.AddDays(1), 3), rules));  // 화요일은 기본
    }

    [Fact]
    public void 비정기_예외는_그_날짜에만_적용된다()
    {
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            new TimetableMappingRule("국", 키("국어(교사C(c))"),
                MappingScope.ForDate(월, 3), true),
        };

        Assert.Equal("교사C", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal("교사A", 교사(new TimetableCell(다음월, 3), rules));   // 다음 주는 기본으로 돌아온다
    }

    [Fact]
    public void 비정기_예외가_정기_예외를_이긴다()
    {
        // 매주 월 3교시는 B선생님인데, 이번 주만 C선생님
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            new TimetableMappingRule("국", 키("국어(교사B(b))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 3), true),
            new TimetableMappingRule("국", 키("국어(교사C(c))"),
                MappingScope.ForDate(월, 3), true),
        };

        Assert.Equal("교사C", 교사(new TimetableCell(월, 3), rules));       // 그 날만 C
        Assert.Equal("교사B", 교사(new TimetableCell(다음월, 3), rules));   // 다음 주는 정기 예외대로 B
    }

    [Fact]
    public void 입력_안_함도_예외로_쓸_수_있다()
    {
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            TimetableMappingRule.Skip("국", MappingScope.ForDate(월, 3)),
        };

        Assert.Equal("입력 안 함", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal("교사A", 교사(new TimetableCell(다음월, 3), rules));
    }

    [Fact]
    public void 나이스에_없는_활동은_입력_안_함으로_매듭짓는다()
    {
        // 봉사활동은 나이스 메뉴에 없다 — 이 선택지가 없으면 실행이 영영 막힌다(실측)
        var rules = new[] { TimetableMappingRule.Skip("봉", MappingScope.Default) };

        var plan = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 5), "봉") },
            rules, 카탈로그, TimetableScreenState.Empty);

        Assert.Equal(AssignmentStatus.Skipped, plan.Assignments[0].Status);
        Assert.False(plan.Assignments[0].IsBlocking);
    }

    [Fact]
    public void 과목이_다르면_국어_규칙에_걸리지_않는다()
    {
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            new TimetableMappingRule("수", 키("수학(교사A(a))"), MappingScope.Default, true),
        };

        Assert.Equal("교사A", 교사(new TimetableCell(월, 1), rules, "수"));
        Assert.Equal("수학", 카탈로그.Find(키("수학(교사A(a))"))!.Subject);
    }

    [Fact]
    public void 기본을_안_정하면_미해결이다()
    {
        // 예외만 있고 기본이 없으면 나머지 칸은 갈 곳이 없다
        var rules = new[]
        {
            new TimetableMappingRule("국", 키("국어(교사B(b))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 3), true),
        };

        Assert.Equal("교사B", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal(nameof(MappingResolutionKind.Unresolved), 교사(new TimetableCell(월, 1), rules));
    }
}
