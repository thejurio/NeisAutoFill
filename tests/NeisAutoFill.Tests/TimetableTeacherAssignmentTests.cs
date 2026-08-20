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

    [Fact]
    public void 나이스에_없는_과목도_다른_과목으로_이을_수_있다()
    {
        // 문서는 "정보"인데 나이스에는 "실과"만 있는 경우가 흔하다.
        // [입력 안 함]밖에 못 고르면 진짜 수업이 조용히 빠진다 — 다른 과목으로 이어야 한다.
        var rules = new[]
        {
            new TimetableMappingRule("정보", 키("수학(교사A(a))"), MappingScope.Default, true),
        };

        var plan = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 4), "정보") },
            rules, 카탈로그, TimetableScreenState.Empty);

        Assert.Equal(AssignmentStatus.Pending, plan.Assignments[0].Status);
        Assert.True(plan.CanRun);
        Assert.Equal("수학", 카탈로그.Find(plan.Assignments[0].TargetStableKey)!.Subject);
    }

    [Fact]
    public void 이은_과목에도_예외를_걸_수_있다()
    {
        // "정보"를 실과로 이어 두고, 그중 월요일 4교시만 다른 교사로
        var rules = new[]
        {
            new TimetableMappingRule("정보", 키("국어(교사A(a))"), MappingScope.Default, true),
            new TimetableMappingRule("정보", 키("국어(교사B(b))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 4), true),
        };

        Assert.Equal("교사B", 교사(new TimetableCell(월, 4), rules, "정보"));
        Assert.Equal("교사A", 교사(new TimetableCell(월, 5), rules, "정보"));
    }
    [Fact]
    public void 모르는_표기도_한_번_이어_두면_그대로_쓰인다()
    {
        // "융"(학교자율시간)처럼 학교마다 다른 글자 — 사용자가 한 번 나이스 과목으로 이어 주면
        // 규칙은 원본 표기로 저장되므로 다음에도 그대로 풀린다.
        var rules = new[]
        {
            new TimetableMappingRule("융", 키("수학(교사A(a))"), MappingScope.Default, true),
        };

        var plan = TimetablePlanBuilder.Build(
            new[]
            {
                new TimetableSourceLesson(new TimetableCell(월, 2), "융"),
                new TimetableSourceLesson(new TimetableCell(다음월, 2), "융"),
            },
            rules, 카탈로그, TimetableScreenState.Empty);

        Assert.All(plan.Assignments, a => Assert.Equal(AssignmentStatus.Pending, a.Status));
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void 이어_주지_않은_표기는_실행을_막는다()
    {
        // 짐작해서 넣으면 엉뚱한 과목이 들어간다. 모르면 멈추는 것이 옳다.
        var plan = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 2), "융") },
            Array.Empty<TimetableMappingRule>(), 카탈로그, TimetableScreenState.Empty);

        Assert.Equal(AssignmentStatus.MappingUnresolved, plan.Assignments[0].Status);
        Assert.False(plan.CanRun);
    }

    // ── 전담 = 그 시간만 바꾸는 것 ─────────────────────────

    [Fact]
    public void 전담은_과목_기본이_아니라_그_시간만_바꾼다()
    {
        // 문서가 초록으로 표시한 시간만 전담이고, 나머지는 담임 그대로다.
        // "국어 담당은 B선생님" 이 아니라 "월 3교시 국어만 B선생님" 이다.
        var rules = new[]
        {
            기본("국어(교사A(a))"),                                   // 담임
            new TimetableMappingRule("국", 키("국어(교사B(b))"),        // 전담 시간
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 3), true),
        };

        Assert.Equal("교사B", 교사(new TimetableCell(월, 3), rules));      // 초록이던 시간
        Assert.Equal("교사A", 교사(new TimetableCell(월, 1), rules));      // 같은 과목 다른 시간
        Assert.Equal("교사A", 교사(new TimetableCell(월.AddDays(2), 3), rules));
    }

    [Fact]
    public void 과목이_통째로_전담이면_그_요일_교시마다_예외가_생긴다()
    {
        // 모든 영어 시간이 초록이면, 영어가 놓인 요일·교시마다 예외가 하나씩 생긴다.
        // 결과는 "영어는 전부 전담" 과 같지만, 근거가 칸에 남아 되돌리기 쉽다.
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            new TimetableMappingRule("국", 키("국어(교사C(c))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 1), true),
            new TimetableMappingRule("국", 키("국어(교사C(c))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 3), true),
        };

        Assert.Equal("교사C", 교사(new TimetableCell(월, 1), rules));
        Assert.Equal("교사C", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal("교사C", 교사(new TimetableCell(다음월, 3), rules));   // 매주
        Assert.Equal("교사A", 교사(new TimetableCell(월, 2), rules));       // 초록이 아니던 시간
    }

    [Fact]
    public void 사람이_고친_예외가_전담_예외를_덮는다()
    {
        // 전담 예외는 요일·교시(정기)로 걸리고, 사람이 칸을 눌러 만든 것은 날짜(비정기)다.
        // 더 구체적인 쪽이 이기므로 그 날만 사람이 고친 대로 간다.
        var rules = new[]
        {
            기본("국어(교사A(a))"),
            new TimetableMappingRule("국", 키("국어(교사B(b))"),
                MappingScope.ForDayPeriod(DayOfWeek.Monday, 3), true),
            new TimetableMappingRule("국", 키("국어(교사C(c))"),
                MappingScope.ForDate(월, 3), true),
        };

        Assert.Equal("교사C", 교사(new TimetableCell(월, 3), rules));
        Assert.Equal("교사B", 교사(new TimetableCell(다음월, 3), rules));
    }

}
