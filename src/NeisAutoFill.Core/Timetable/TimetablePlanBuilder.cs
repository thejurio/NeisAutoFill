namespace NeisAutoFill.Core.Timetable;

/// <summary>원본에서 읽어낸 수업 한 칸 (문서 인식 결과 — 나이스 항목이 아니다).</summary>
/// <param name="Cell">날짜+교시</param>
/// <param name="SourceToken">원본 표기 ("국", "창" 등)</param>
public sealed record TimetableSourceLesson(TimetableCell Cell, string SourceToken);

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
    public static TimetablePlan Build(
        IEnumerable<TimetableSourceLesson> lessons,
        IEnumerable<TimetableMappingRule> rules,
        TimetableCatalog catalog,
        TimetableScreenState screen)
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
            result.Add(Classify(lesson, ruleList, catalog, screen, duplicated));

        return new TimetablePlan(result);
    }

    /// <summary>
    /// 지금 셀 값이 목표와 같은가 (기술설계 R-006).
    /// <b>저장 전 셀에는 계정이 빠진다</b> — 방금 입력한 칸을 "기존에 다른 수업이 있다"고 오판하면
    /// 재실행할 때마다 전부 충돌로 잠긴다. 실제로 한 주 입력 뒤 재계획에서 그 현상을 확인했다.
    /// 계정이 빈 키는 <see cref="NeisTimetableOption.LooseKey"/> 로 비교하되,
    /// 그 키가 카탈로그에서 유일할 때만 인정한다(동명이인 방지, D-006).
    /// </summary>
    private static bool SameValue(string current, NeisTimetableOption target, TimetableCatalog catalog)
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
        IReadOnlySet<TimetableCell> duplicated)
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

        return At(AssignmentStatus.ExistingValueConflict,
            "기존에 다른 수업이 들어 있습니다. 덮어쓰려면 확인이 필요합니다.", target);
    }
}
