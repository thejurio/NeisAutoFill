using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 실행 계획 생성과 분류 (기술설계 §12, 로드맵 T6).
/// 클릭 전에 연간 전체가 여기서 분류되고, 막힌 게 있으면 실행이 잠긴다.
/// </summary>
public class TimetablePlanBuilderTests
{
    private static readonly DateOnly 월 = new(2026, 8, 24);

    private static readonly string[] 메뉴 =
    {
        "국어(교사A(account-a))",
        "체육(교사A(account-a))",
        "체육(교사B(account-b))",
        "취소",
    };

    private static TimetableCatalog 카탈로그 => new(TimetableMenuParser.ParseAll(메뉴));

    private static string 키(string menuText) => TimetableMenuParser.Parse(menuText).StableKey;

    private static TimetableScreenState 화면(
        Dictionary<TimetableCell, string>? current = null,
        HashSet<TimetableCell>? unavailable = null)
        => new(current ?? new Dictionary<TimetableCell, string>(),
               unavailable ?? new HashSet<TimetableCell>());

    private static TimetablePlan 계획(
        IEnumerable<TimetableSourceLesson> lessons,
        IEnumerable<TimetableMappingRule> rules,
        TimetableScreenState? screen = null)
        => TimetablePlanBuilder.Build(lessons, rules, 카탈로그, screen ?? 화면());

    [Fact]
    public void 빈_셀에_확정된_대상은_입력_예정이다()
    {
        var cell = new TimetableCell(월, 1);
        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

        Assert.Equal(AssignmentStatus.Pending, p.Assignments[0].Status);
        Assert.True(p.CanRun);
    }

    [Fact]
    public void 이미_같은_값이면_건너뛴다()
    {
        var cell = new TimetableCell(월, 1);
        var target = 키("국어(교사A(account-a))");

        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", target, MappingScope.Default) },
            화면(new Dictionary<TimetableCell, string> { [cell] = target }));

        Assert.Equal(AssignmentStatus.AlreadyMatches, p.Assignments[0].Status);
        Assert.False(p.Assignments[0].WillWrite);   // 멱등성 — 다시 눌러도 변경 0건
    }

    [Fact]
    public void 기존_값이_다르면_충돌로_막는다()
    {
        var cell = new TimetableCell(월, 1);

        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) },
            화면(new Dictionary<TimetableCell, string> { [cell] = 키("체육(교사B(account-b))") }));

        Assert.Equal(AssignmentStatus.ExistingValueConflict, p.Assignments[0].Status);
        Assert.False(p.CanRun);   // 기본 덮어쓰기 금지(D-010)
    }

    [Fact]
    public void 매핑이_없으면_미해결이다()
    {
        var p = 계획(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "국") },
            Array.Empty<TimetableMappingRule>());

        Assert.Equal(AssignmentStatus.MappingUnresolved, p.Assignments[0].Status);
        Assert.False(p.CanRun);
    }

    [Fact]
    public void 창은_종류_미정으로_따로_안내한다()
    {
        var p = 계획(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 3), "창") },
            Array.Empty<TimetableMappingRule>());

        Assert.Equal(AssignmentStatus.CreativeUnresolved, p.Assignments[0].Status);
        Assert.Contains("자율", p.Assignments[0].Reason);
    }

    [Fact]
    public void 규칙이_갈리면_충돌이다()
    {
        var p = 계획(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 3), "체") },
            new[]
            {
                new TimetableMappingRule("체", 키("체육(교사A(account-a))"), MappingScope.ForDay(DayOfWeek.Monday)),
                new TimetableMappingRule("체", 키("체육(교사B(account-b))"), MappingScope.ForDay(DayOfWeek.Monday)),
            });

        Assert.Equal(AssignmentStatus.MappingConflict, p.Assignments[0].Status);
    }

    [Fact]
    public void 대상이_목록에서_사라지면_실행하지_않는다()
    {
        // 저장된 규칙의 교사가 전근 등으로 빠진 상황
        var p = 계획(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "국") },
            new[] { new TimetableMappingRule("국", "L|국어|사라진교사|account-z", MappingScope.Default) });

        Assert.Equal(AssignmentStatus.OptionNotFound, p.Assignments[0].Status);
        Assert.False(p.CanRun);
    }

    [Fact]
    public void 수업_없는_날은_실행을_막지_않는다()
    {
        var cell = new TimetableCell(월, 1);

        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) },
            화면(unavailable: new HashSet<TimetableCell> { cell }));

        Assert.Equal(AssignmentStatus.CellUnavailable, p.Assignments[0].Status);
        Assert.False(p.Assignments[0].IsBlocking);   // 정상적인 건너뜀 (R-005)
    }

    [Fact]
    public void 입력_안_함은_막지_않는다()
    {
        var p = 계획(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "국") },
            new[] { TimetableMappingRule.Skip("국", MappingScope.Default) });

        Assert.Equal(AssignmentStatus.Skipped, p.Assignments[0].Status);
        Assert.False(p.Assignments[0].IsBlocking);
    }

    [Fact]
    public void 같은_셀을_두_원본이_노리면_충돌이다()
    {
        var cell = new TimetableCell(월, 1);

        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국"), new TimetableSourceLesson(cell, "체") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

        Assert.All(p.Assignments, a => Assert.Equal(AssignmentStatus.DuplicateTarget, a.Status));
        Assert.False(p.CanRun);
    }

    [Fact]
    public void 막힌_항목이_하나라도_있으면_실행이_잠긴다()
    {
        var p = 계획(
            new[]
            {
                new TimetableSourceLesson(new TimetableCell(월, 1), "국"),
                new TimetableSourceLesson(new TimetableCell(월, 2), "미지의과목"),
            },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

        Assert.Single(p.Writable);
        Assert.Single(p.Blocking);
        Assert.False(p.CanRun);   // 부분 진행은 사용자가 명시적으로 범위를 좁힐 때만
    }

    [Fact]
    public void 주_단위로_나눈다()
    {
        var p = 계획(
            new[]
            {
                new TimetableSourceLesson(new TimetableCell(월, 1), "국"),
                new TimetableSourceLesson(new TimetableCell(월.AddDays(7), 1), "국"),
            },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

        Assert.Equal(2, p.ByWeek.Count);
        Assert.Equal(월, p.ByWeek[0].Key);   // 주 시작일 = 월요일
    }

    [Fact]
    public void 같은_입력이면_항상_같은_계획이_나온다()
    {
        var lessons = new[]
        {
            new TimetableSourceLesson(new TimetableCell(월, 1), "국"),
            new TimetableSourceLesson(new TimetableCell(월, 2), "체"),
        };
        var rules = new[]
        {
            new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default),
            new TimetableMappingRule("체", 키("체육(교사B(account-b))"), MappingScope.Default),
        };

        var a = 계획(lessons, rules);
        var b = 계획(lessons, rules);

        Assert.Equal(a.Assignments, b.Assignments);   // 결정적 (T6 완료 기준)
    }

    [Fact]
    public void 분류별_집계를_제공한다()
    {
        var p = 계획(
            new[]
            {
                new TimetableSourceLesson(new TimetableCell(월, 1), "국"),
                new TimetableSourceLesson(new TimetableCell(월, 2), "창"),
            },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

        Assert.Equal(1, p.CountByStatus[AssignmentStatus.Pending]);
        Assert.Equal(1, p.CountByStatus[AssignmentStatus.CreativeUnresolved]);
    }

    [Fact]
    public void 나이스_학기에_없는_날짜는_입력_예정이_아니다()
    {
        // 다른 학년도 문서를 넣으면 존재하지 않는 날에 입력하려 들게 된다 —
        // 실제로 2025학년도 시간표를 2026학년도 화면에 태워 392칸이 모두 '입력 예정'이 되는 것을 확인했다.
        var 작년 = new TimetableCell(new DateOnly(2025, 3, 3), 1);

        var p = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(작년, "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) },
            카탈로그,
            new TimetableScreenState(new Dictionary<TimetableCell, string>(), new HashSet<TimetableCell>(),
                TermStart: new DateOnly(2026, 8, 19), TermEnd: new DateOnly(2027, 2, 28)));

        Assert.Equal(AssignmentStatus.OutOfRange, p.Assignments[0].Status);
        Assert.True(p.Assignments[0].IsBlocking);
        Assert.False(p.CanRun);
    }

    [Fact]
    public void 학기_범위를_모르면_검사하지_않는다()
    {
        var p = 계획(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

        Assert.Equal(AssignmentStatus.Pending, p.Assignments[0].Status);
    }
}
