namespace NeisAutoFill.Core.Timetable;

/// <summary>원본에서 읽어낸 수업 한 칸 (문서 인식 결과 — 나이스 항목이 아니다).</summary>
/// <param name="Cell">날짜+교시</param>
/// <param name="SourceToken">원본 표기 ("국", "창" 등)</param>
/// <param name="Specialist">
/// 전담 교사가 맡는 시간인가. 이지에듀는 이것을 <b>초록 글씨</b>로 표시한다.
/// 표시가 없는 문서에서는 언제나 false — 그때는 담임으로 본다.
/// </param>
public sealed record TimetableSourceLesson(TimetableCell Cell, string SourceToken, bool Specialist = false);

/// <summary>계획을 만들 때 필요한 나이스 쪽 현재 상태 (브라우저 없이 테스트할 수 있도록 값으로 받는다).</summary>
/// <param name="CurrentValues">셀 → 현재 값의 안정 키. 비어 있는 셀은 넣지 않는다</param>
/// <param name="UnavailableCells">휴업일 등 메뉴를 열 수 없는 셀</param>
/// <param name="TermStart">나이스가 아는 학기 시작일. null 이면 범위를 검사하지 않는다</param>
/// <param name="TermEnd">학기 종료일</param>
public sealed record TimetableScreenState(
    IReadOnlyDictionary<TimetableCell, string> CurrentValues,
    IReadOnlySet<TimetableCell> UnavailableCells,
    DateOnly? TermStart = null,
    DateOnly? TermEnd = null)
{
    public static TimetableScreenState Empty { get; } =
        new(new Dictionary<TimetableCell, string>(), new HashSet<TimetableCell>());

    /// <summary>
    /// 그 날짜가 나이스 학기 안에 있는가.
    /// 다른 학년도·학기 문서를 넣으면 존재하지도 않는 날에 입력하려 들게 된다 —
    /// 실제로 2025학년도 시간표를 2026학년도 화면에 태워 392칸이 모두 '입력 예정'으로 잡히는 것을 확인했다.
    /// </summary>
    public bool IsInTerm(DateOnly date) =>
        (TermStart is null || date >= TermStart) && (TermEnd is null || date <= TermEnd);
}

/// <summary>
/// 원본 수업 + 매핑 규칙 + 카탈로그 + 나이스 현재 값 → 셀별 실행 계획 (기술설계 §12).
///
/// <b>클릭 한 번 하기 전에</b> 연간 전체를 여기서 분류한다. 같은 입력이면 항상 같은 결과가 나온다.
/// </summary>
public static class TimetablePlanBuilder
{
    /// <param name="allowOverwrite">
    /// 이미 다른 값이 들어 있는 칸을 덮어쓸지 (D-010).
    /// <b>기본은 false</b> — 사용자가 동의 창에서 명시적으로 허용했을 때만 true 로 넘긴다.
    /// 나이스 학급시간표는 기준시간표로 일괄 생성돼 이미 차 있는 경우가 흔하다.
    /// </param>
    public static TimetablePlan Build(
        IEnumerable<TimetableSourceLesson> lessons,
        IEnumerable<TimetableMappingRule> rules,
        TimetableCatalog catalog,
        TimetableScreenState screen,
        bool allowOverwrite = false)
    {
        var ruleList = rules.ToList();
        var lessonList = lessons.ToList();

        // 같은 셀을 두 원본이 노리는 경우 — 어느 쪽이 맞는지 코드가 정할 수 없다
        var duplicated = lessonList
            .GroupBy(l => l.Cell)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet();

        var result = new List<TimetableAssignment>(lessonList.Count);
        foreach (var lesson in lessonList)
            result.Add(Classify(lesson, ruleList, catalog, screen, duplicated, allowOverwrite));

        if (allowOverwrite)
            result.AddRange(FindExtras(lessonList, catalog, screen));

        return new TimetablePlan(result);
    }

    /// <summary>
    /// 문서에는 없는데 나이스에만 남아 있는 수업을 찾아 <b>지울 대상</b>으로 만든다.
    ///
    /// 나이스 학급시간표는 기준시간표로 일괄 생성되므로, 문서보다 교시가 많은 날이 흔하다
    /// (예: 나이스는 6교시까지, 문서는 4교시까지). 그냥 두면 문서와 영영 달라진다.
    ///
    /// <b>네 겹의 안전장치를 둔다:</b>
    /// <list type="number">
    /// <item><b>문서에 수업이 한 칸이라도 있는 날만</b> 본다. 문서가 아예 다루지 않는 날
    ///       (주말·공휴일·문서 범위 밖)은 통째로 건드리지 않는다.</item>
    /// <item><b>꼬리만 지운다.</b> 그 날 문서의 마지막 교시보다 <b>뒤</b>에 있는 칸만 지운다.
    ///       가운데 빈 교시(<b>구멍</b>)는 남긴다 — 문서가 그 교시를 못 읽었을 수 있는데,
    ///       "진짜 비었다"와 "못 읽었다"를 값만 보고는 가를 수 없다. 못 가르면 지우지 않는다.</item>
    /// <item><b>그 요일의 평소보다 짧은 날은 통째로 넘어간다.</b> 같은 요일 다른 주들이 6교시인데
    ///       이 날만 4교시로 읽혔다면 뒤쪽을 통째로 놓쳤을 가능성이 크다. 단축수업일 수도 있지만,
    ///       <b>잘못 지우는 손해가 안 지우는 손해보다 훨씬 크다.</b></item>
    /// <item><b>지금 값이 카탈로그에 있는 수업일 때만</b> 지운다. 즉 <b>우리가 넣을 수 있었던 것만</b> 되돌린다.
    ///       행사·현장체험학습·휴업일 표시처럼 학교가 따로 넣은 것은 남긴다.</item>
    /// </list>
    /// 덮어쓰기를 켰을 때만 동작한다 — "문서대로 맞춘다"는 뜻이 이미 그 선택에 들어 있다.
    /// </summary>
    private static IEnumerable<TimetableAssignment> FindExtras(
        IReadOnlyList<TimetableSourceLesson> lessons,
        TimetableCatalog catalog,
        TimetableScreenState screen)
    {
        var planned = lessons.Select(l => l.Cell).ToHashSet();

        // 날짜 → 그 날 문서의 마지막 교시. 여기 없는 날은 문서가 안 다루는 날이다.
        var lastPeriod = lessons
            .GroupBy(l => l.Cell.Date)
            .ToDictionary(g => g.Key, g => g.Max(l => l.Cell.Period));

        // 요일 → 그 요일의 <b>평소</b> 마지막 교시(최빈값). 이 날만 유독 짧으면 파싱을 의심한다.
        var usualLast = lastPeriod
            .GroupBy(kv => kv.Key.DayOfWeek)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(kv => kv.Value)
                      .OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key)
                      .First().Key);

        foreach (var (cell, current) in screen.CurrentValues.OrderBy(c => c.Key.Date).ThenBy(c => c.Key.Period))
        {
            if (planned.Contains(cell)) continue;              // 문서가 쓸 칸이다
            if (!lastPeriod.TryGetValue(cell.Date, out var last)) continue;   // ① 문서가 안 다루는 날
            if (cell.Period < last) continue;                  // ② 구멍 — 못 읽었을 수 있다
            if (usualLast.TryGetValue(cell.Date.DayOfWeek, out var usual) && last < usual)
                continue;                                      // ③ 그 요일 평소보다 짧다 — 뒤를 놓쳤을 수 있다
            if (!screen.IsInTerm(cell.Date)) continue;
            if (screen.UnavailableCells.Contains(cell)) continue;
            if (string.IsNullOrEmpty(current)) continue;

            // ② 우리가 넣을 수 있었던 수업만 되돌린다
            if (catalog.Find(current) is null &&
                (!current.EndsWith('|') || catalog.FindLoose(current[..^1]) is null))
                continue;

            yield return new TimetableAssignment(
                cell, "", AssignmentStatus.ExtraToClear,
                CurrentStableKey: current,
                Reason: "문서에 없는 수업이라 지웁니다.",
                Overwrite: true);
        }
    }

    /// <summary>
    /// 지금 셀 값이 목표와 같은가 (기술설계 R-006).
    /// <b>저장 전 셀에는 계정이 빠진다</b> — 방금 입력한 칸을 "기존에 다른 수업이 있다"고 오판하면
    /// 재실행할 때마다 전부 충돌로 잠긴다. 실제로 한 주 입력 뒤 재계획에서 그 현상을 확인했다.
    /// 계정이 빈 키는 <see cref="NeisTimetableOption.LooseKey"/> 로 비교하되,
    /// 그 키가 카탈로그에서 유일할 때만 인정한다(동명이인 방지, D-006).
    /// </summary>
    public static bool SameValue(string current, NeisTimetableOption target, TimetableCatalog catalog)
    {
        if (current == target.StableKey) return true;

        // "L|국어|해시|" 처럼 마지막 칸이 비어 있으면 저장 전 표기다
        if (!current.EndsWith('|')) return false;

        var loose = current[..^1];
        return loose == target.LooseKey && catalog.FindLoose(loose) is not null;
    }

    private static TimetableAssignment Classify(
        TimetableSourceLesson lesson,
        IReadOnlyList<TimetableMappingRule> rules,
        TimetableCatalog catalog,
        TimetableScreenState screen,
        IReadOnlySet<TimetableCell> duplicated,
        bool allowOverwrite)
    {
        var cell = lesson.Cell;
        var token = lesson.SourceToken;
        screen.CurrentValues.TryGetValue(cell, out var current);
        current ??= "";

        TimetableAssignment At(AssignmentStatus status, string reason, string target = "") =>
            new(cell, token, status, target, current, reason);

        // ① 원본 자체를 못 읽은 칸
        if (string.IsNullOrWhiteSpace(token))
            return At(AssignmentStatus.SourceUnresolved, "원본에서 수업을 읽지 못했습니다.");

        // ② 같은 셀을 두 원본이 노림 — 매핑을 보기 전에 막는다
        if (duplicated.Contains(cell))
            return At(AssignmentStatus.DuplicateTarget, "같은 날짜·교시에 원본 수업이 둘 이상입니다.");

        // ③ 나이스 학기 범위 밖 — 그 날짜는 화면에 아예 없다
        if (!screen.IsInTerm(cell.Date))
            return At(AssignmentStatus.OutOfRange,
                $"{cell.Date:yyyy-MM-dd} 은 나이스의 이 학기에 없는 날짜입니다. 문서의 학년도·학기를 확인하세요.");

        // ④ 휴업일 등 셀을 못 쓰는 경우 (실패가 아니라 정상적인 건너뜀 상태)
        if (screen.UnavailableCells.Contains(cell))
            return At(AssignmentStatus.CellUnavailable, "수업이 없는 날이라 입력할 수 없습니다.");

        // ⑤ 매핑 해석
        var resolution = TimetableMappingResolver.Resolve(token, cell, rules);
        switch (resolution.Kind)
        {
            case MappingResolutionKind.Skip:
                return At(AssignmentStatus.Skipped, resolution.Describe());

            case MappingResolutionKind.Conflict:
                return At(AssignmentStatus.MappingConflict, resolution.Describe());

            case MappingResolutionKind.Unresolved:
                // 창은 "아직 종류를 안 정했다"가 더 정확한 안내다
                var normalized = TimetableTokenNormalizer.Normalize(token);
                return normalized.IsCreativeUnresolved
                    ? At(AssignmentStatus.CreativeUnresolved,
                        "창의적 체험활동의 종류(자율·자치/동아리/진로)가 정해지지 않았습니다.")
                    : At(AssignmentStatus.MappingUnresolved, "어느 과목·교사로 넣을지 정해지지 않았습니다.");
        }

        var target = resolution.TargetStableKey;

        // ⑥ 대상이 지금 메뉴에 실제로 있는지 — 저장된 규칙의 교사가 사라졌을 수 있다
        if (catalog.Find(target) is null)
            return At(AssignmentStatus.OptionNotFound,
                "지정한 과목·교사가 현재 나이스 목록에 없습니다.", target);

        // ⑦ 현재 값과 비교
        if (current.Length == 0)
            return At(AssignmentStatus.Pending, resolution.Describe(), target);

        if (SameValue(current, catalog.Find(target)!, catalog))
            return At(AssignmentStatus.AlreadyMatches, "이미 목표와 같습니다.", target);

        return allowOverwrite
            ? At(AssignmentStatus.Pending, "기존 값을 덮어씁니다.", target) with { Overwrite = true }
            : At(AssignmentStatus.ExistingValueConflict,
                "기존에 다른 수업이 들어 있습니다. 덮어쓰려면 확인이 필요합니다.", target);
    }
}
