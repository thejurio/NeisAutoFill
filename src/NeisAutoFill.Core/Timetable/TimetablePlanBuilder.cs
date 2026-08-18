namespace NeisAutoFill.Core.Timetable;

/// <summary>원본에서 읽어낸 수업 한 칸 (문서 인식 결과 — 나이스 항목이 아니다).</summary>
/// <param name="Cell">날짜+교시</param>
/// <param name="SourceToken">원본 표기 ("국", "창" 등)</param>
public sealed record TimetableSourceLesson(TimetableCell Cell, string SourceToken);

/// <summary>계획을 만들 때 필요한 나이스 쪽 현재 상태 (브라우저 없이 테스트할 수 있도록 값으로 받는다).</summary>
/// <param name="CurrentValues">셀 → 현재 값의 안정 키. 비어 있는 셀은 넣지 않는다</param>
/// <param name="UnavailableCells">휴업일 등 메뉴를 열 수 없는 셀</param>
public sealed record TimetableScreenState(
    IReadOnlyDictionary<TimetableCell, string> CurrentValues,
    IReadOnlySet<TimetableCell> UnavailableCells)
{
    public static TimetableScreenState Empty { get; } =
        new(new Dictionary<TimetableCell, string>(), new HashSet<TimetableCell>());
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

        // ③ 휴업일 등 셀을 못 쓰는 경우 (실패가 아니라 정상적인 건너뜀 상태)
        if (screen.UnavailableCells.Contains(cell))
            return At(AssignmentStatus.CellUnavailable, "수업이 없는 날이라 입력할 수 없습니다.");

        // ④ 매핑 해석
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

        // ⑤ 대상이 지금 메뉴에 실제로 있는지 — 저장된 규칙의 교사가 사라졌을 수 있다
        if (catalog.Find(target) is null)
            return At(AssignmentStatus.OptionNotFound,
                "지정한 과목·교사가 현재 나이스 목록에 없습니다.", target);

        // ⑥ 현재 값과 비교
        if (current.Length == 0)
            return At(AssignmentStatus.Pending, resolution.Describe(), target);

        if (current == target)
            return At(AssignmentStatus.AlreadyMatches, "이미 목표와 같습니다.", target);

        return At(AssignmentStatus.ExistingValueConflict,
            "기존에 다른 수업이 들어 있습니다. 덮어쓰려면 확인이 필요합니다.", target);
    }
}
