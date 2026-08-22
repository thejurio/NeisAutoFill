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

    [Fact]
    public void 저장_전_표기도_이미_같음으로_본다()
    {
        // 저장 전 셀에는 계정이 빠진다(R-006). 이걸 모르면 방금 입력한 칸이
        // 전부 '기존 값 충돌'로 잠긴다 — 실제로 한 주 입력 뒤 재계획에서 겪었다.
        var cell = new TimetableCell(월, 1);
        var target = TimetableMenuParser.Parse("국어(교사A(account-a))");
        var 저장전 = target.LooseKey + "|";   // 계정 칸이 빈 형태

        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", target.StableKey, MappingScope.Default) },
            화면(new Dictionary<TimetableCell, string> { [cell] = 저장전 }));

        Assert.Equal(AssignmentStatus.AlreadyMatches, p.Assignments[0].Status);
    }

    [Fact]
    public void 덮어쓰기를_허용하면_기존_값이_달라도_입력한다()
    {
        // 나이스 학급시간표는 기준시간표로 이미 채워져 있는 경우가 흔하다(실기기검증 §4-F).
        // 그때는 '빈 칸 채우기'가 아니라 '있는 값 고치기'가 된다 — 사용자가 미리 동의했을 때만.
        var cell = new TimetableCell(월, 1);

        var p = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) },
            카탈로그,
            화면(new Dictionary<TimetableCell, string> { [cell] = 키("체육(교사B(account-b))") }),
            allowOverwrite: true);

        Assert.Equal(AssignmentStatus.Pending, p.Assignments[0].Status);
        Assert.True(p.Assignments[0].Overwrite);
        Assert.True(p.CanRun);
        Assert.Single(p.Overwriting);
    }

    [Fact]
    public void 덮어쓰기를_허용해도_이미_같으면_건드리지_않는다()
    {
        var cell = new TimetableCell(월, 1);
        var target = 키("국어(교사A(account-a))");

        var p = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", target, MappingScope.Default) },
            카탈로그,
            화면(new Dictionary<TimetableCell, string> { [cell] = target }),
            allowOverwrite: true);

        Assert.Equal(AssignmentStatus.AlreadyMatches, p.Assignments[0].Status);
        Assert.Empty(p.Overwriting);   // 멱등성은 덮어쓰기와 무관하게 지켜진다
    }

    [Fact]
    public void 덮어쓰기를_허용해도_빈_칸은_덮어쓰기가_아니다()
    {
        var p = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) },
            카탈로그, 화면(), allowOverwrite: true);

        Assert.Equal(AssignmentStatus.Pending, p.Assignments[0].Status);
        Assert.False(p.Assignments[0].Overwrite);
        Assert.Empty(p.Overwriting);
    }

    [Fact]
    public void 덮어쓰기를_허용해도_다른_막힘은_그대로_막는다()
    {
        // 덮어쓰기 동의는 '기존 값'에 대한 것이지 매핑 미해결까지 통과시키는 것이 아니다
        var p = TimetablePlanBuilder.Build(
            new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "미지의과목") },
            Array.Empty<TimetableMappingRule>(),
            카탈로그, 화면(), allowOverwrite: true);

        Assert.Equal(AssignmentStatus.MappingUnresolved, p.Assignments[0].Status);
        Assert.False(p.CanRun);
    }

    [Fact]
    public void 저장_전_표기여도_다른_과목이면_충돌이다()
    {
        var cell = new TimetableCell(월, 1);
        var 체육저장전 = TimetableMenuParser.Parse("체육(교사B(account-b))").LooseKey + "|";

        var p = 계획(
            new[] { new TimetableSourceLesson(cell, "국") },
            new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) },
            화면(new Dictionary<TimetableCell, string> { [cell] = 체육저장전 }));

        Assert.Equal(AssignmentStatus.ExistingValueConflict, p.Assignments[0].Status);
    }

    // ── 문서에 없는데 나이스에만 남은 수업 지우기 (2026-08-21) ──────────────
    //
    // 나이스 학급시간표는 기준시간표로 일괄 생성돼 문서보다 교시가 많은 날이 흔하다.
    // 지우는 것은 되돌릴 수 없으므로 안전장치 두 겹을 여기서 못 박는다.

    private static TimetablePlan 덮어쓰기계획(
        IEnumerable<TimetableSourceLesson> lessons,
        IEnumerable<TimetableMappingRule> rules,
        TimetableScreenState screen)
        => TimetablePlanBuilder.Build(lessons, rules, 카탈로그, screen, allowOverwrite: true);

    private static (TimetableSourceLesson[] 수업, TimetableMappingRule[] 규칙) 월요일_1교시_국어() =>
        (new[] { new TimetableSourceLesson(new TimetableCell(월, 1), "국") },
         new[] { new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default) });

    [Fact]
    public void 문서에_없는데_나이스에_있는_수업은_지울_대상이다()
    {
        var (수업, 규칙) = 월요일_1교시_국어();
        var 남은칸 = new TimetableCell(월, 2);

        var p = 덮어쓰기계획(수업, 규칙,
            화면(new() { [남은칸] = 키("체육(교사A(account-a))") }));

        var 지움 = Assert.Single(p.Clearing);
        Assert.Equal(남은칸, 지움.Cell);
        Assert.Equal(AssignmentStatus.ExtraToClear, 지움.Status);
        Assert.True(지움.WillWrite);
    }

    [Fact]
    public void 덮어쓰기를_켜지_않으면_지우지_않는다()
    {
        var (수업, 규칙) = 월요일_1교시_국어();

        var p = 계획(수업, 규칙,
            화면(new() { [new TimetableCell(월, 2)] = 키("체육(교사A(account-a))") }));

        Assert.Empty(p.Clearing);
    }

    [Fact]
    public void 문서가_다루지_않는_날은_통째로_건드리지_않는다()
    {
        // 안전장치 ① — 파싱이 어긋난 날을 몽땅 비우는 사고를 막는다
        var (수업, 규칙) = 월요일_1교시_국어();
        var 다른날 = 월.AddDays(1);

        var p = 덮어쓰기계획(수업, 규칙,
            화면(new() { [new TimetableCell(다른날, 3)] = 키("체육(교사A(account-a))") }));

        Assert.Empty(p.Clearing);
    }

    [Fact]
    public void 카탈로그에_없는_값은_지우지_않는다()
    {
        // 안전장치 ② — 행사·현장체험학습처럼 학교가 따로 넣은 것은 남긴다
        var (수업, 규칙) = 월요일_1교시_국어();

        var p = 덮어쓰기계획(수업, 규칙,
            화면(new() { [new TimetableCell(월, 2)] = "현장체험학습" }));

        Assert.Empty(p.Clearing);
    }

    [Fact]
    public void 학기_밖이거나_메뉴가_안_열리는_칸은_지우지_않는다()
    {
        var (수업, 규칙) = 월요일_1교시_국어();
        var 막힌칸 = new TimetableCell(월, 2);
        var 체육 = 키("체육(교사A(account-a))");

        var 막힘 = TimetablePlanBuilder.Build(수업, 규칙, 카탈로그,
            new TimetableScreenState(new Dictionary<TimetableCell, string> { [막힌칸] = 체육 },
                                     new HashSet<TimetableCell> { 막힌칸 }),
            allowOverwrite: true);
        Assert.Empty(막힘.Clearing);

        var 밖 = TimetablePlanBuilder.Build(수업, 규칙, 카탈로그,
            new TimetableScreenState(new Dictionary<TimetableCell, string> { [막힌칸] = 체육 },
                                     new HashSet<TimetableCell>(), 월.AddDays(7), 월.AddDays(30)),
            allowOverwrite: true);
        Assert.Empty(밖.Clearing);
    }

    [Fact]
    public void 문서가_쓸_칸은_지울_대상이_아니다()
    {
        // 같은 칸을 지우면서 넣는 자기모순이 생기면 안 된다
        var (수업, 규칙) = 월요일_1교시_국어();

        var p = 덮어쓰기계획(수업, 규칙,
            화면(new() { [new TimetableCell(월, 1)] = 키("체육(교사A(account-a))") }));

        Assert.Empty(p.Clearing);
        Assert.Equal(AssignmentStatus.Pending, p.Assignments[0].Status);
    }

    // ── 파싱이 일부만 놓쳤을 때 (지적 2026-08-22) ──────────────
    //
    // "문서에 한 칸이라도 있는 날만" 이라는 안전장치는 <b>부분 누락</b>을 못 막는다.
    // 5교시 하나를 못 읽으면 그 칸이 삭제 대상이 되어 멀쩡한 수업이 지워진다.

    private static TimetableSourceLesson 국어(DateOnly 날, int 교시) =>
        new(new TimetableCell(날, 교시), "국");

    private static readonly TimetableMappingRule[] 국어규칙 =
        { new("국", 키("국어(교사A(account-a))"), MappingScope.Default) };

    /// <summary>가운데 빈 교시 = 못 읽었을 수 있다. 못 가르면 지우지 않는다.</summary>
    [Fact]
    public void 가운데_구멍은_지우지_않는다()
    {
        // 문서: 1·2·4교시 (3교시를 놓쳤다) / 나이스: 3교시에 수업이 있다
        var 수업 = new[] { 국어(월, 1), 국어(월, 2), 국어(월, 4) };
        var 구멍 = new TimetableCell(월, 3);

        var p = 덮어쓰기계획(수업, 국어규칙, 화면(new() { [구멍] = 키("체육(교사A(account-a))") }));

        Assert.Empty(p.Clearing);
    }

    /// <summary>꼬리 = 문서가 그 날을 여기까지로 본 것. 이것은 지운다.</summary>
    [Fact]
    public void 마지막_교시_뒤쪽은_지운다()
    {
        // 문서: 1·2교시 / 나이스: 3교시까지 있다 → 3교시는 꼬리
        var 수업 = new[] { 국어(월, 1), 국어(월, 2) };
        var 꼬리 = new TimetableCell(월, 3);

        var p = 덮어쓰기계획(수업, 국어규칙, 화면(new() { [꼬리] = 키("체육(교사A(account-a))") }));

        Assert.Equal(꼬리, Assert.Single(p.Clearing).Cell);
    }

    /// <summary>
    /// 같은 요일 다른 주가 6교시인데 이 주만 4교시로 읽혔다 —
    /// 뒤쪽을 통째로 놓쳤을 수 있으므로 그 날은 손대지 않는다.
    /// </summary>
    [Fact]
    public void 그_요일_평소보다_짧은_날은_통째로_넘어간다()
    {
        var 다음월 = 월.AddDays(7);
        var 수업 = new[]
        {
            국어(월, 1), 국어(월, 2), 국어(월, 3),                    // 이 주는 3교시까지만 읽혔다
            국어(다음월, 1), 국어(다음월, 2), 국어(다음월, 3),
            국어(다음월, 4), 국어(다음월, 5),                          // 평소 월요일은 5교시
        };

        var p = 덮어쓰기계획(수업, 국어규칙,
            화면(new() { [new TimetableCell(월, 4)] = 키("체육(교사A(account-a))") }));

        Assert.Empty(p.Clearing);
    }

    /// <summary>평소와 같은 길이면 꼬리를 지운다 — 진짜 짧은 요일(금요일 등)을 막지 않는다.</summary>
    [Fact]
    public void 평소와_같은_길이면_꼬리를_지운다()
    {
        var 다음월 = 월.AddDays(7);
        var 수업 = new[]
        {
            국어(월, 1), 국어(월, 2),
            국어(다음월, 1), 국어(다음월, 2),      // 월요일은 늘 2교시까지
        };

        var p = 덮어쓰기계획(수업, 국어규칙,
            화면(new() { [new TimetableCell(월, 3)] = 키("체육(교사A(account-a))") }));

        Assert.Single(p.Clearing);
    }
}
