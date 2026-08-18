namespace NeisAutoFill.Core.Timetable;

/// <summary>창체 칸 하나를 어떻게 정했는지.</summary>
public enum CreativeLinkStatus
{
    /// <summary>원본 시간표에 종류까지 적혀 있었다 (자·동·진).</summary>
    FromSource,
    /// <summary>창체 계획에서 그 날의 종류를 찾아 정했다.</summary>
    FromPlan,
    /// <summary>정하지 못했다 — 사용자가 골라야 한다.</summary>
    Unresolved,
    /// <summary>근거가 서로 어긋난다 — 임의로 고르지 않는다.</summary>
    Conflict,
}

/// <summary>창체 칸 하나의 연결 결과.</summary>
/// <param name="Cell">날짜+교시</param>
/// <param name="SourceToken">원본 표기 (창·자·동·진·봉)</param>
/// <param name="Kind">확정된 종류. 정하지 못했으면 Unresolved</param>
/// <param name="ActivityName">연결된 활동명 (있으면)</param>
/// <param name="Reason">왜 이렇게 정해졌는지 — 화면에 그대로 보여 준다</param>
public sealed record CreativeLink(
    TimetableCell Cell,
    string SourceToken,
    CreativeActivityKind Kind,
    string? ActivityName,
    CreativeLinkStatus Status,
    string Reason)
{
    public bool IsResolved => Status is CreativeLinkStatus.FromSource or CreativeLinkStatus.FromPlan;
}

/// <summary>
/// 연간 시간표의 창체 칸과 창체 계획을 잇는다 (기술설계 §8 "창 셀과 창체 이벤트 연결").
///
/// 원칙:
/// <list type="bullet">
/// <item>원본에 종류가 적혀 있으면(자·동·진) 그대로 쓴다 — 가장 확실한 근거다</item>
/// <item>미분류(창·봉)는 그 날 창체 계획에서 종류를 가져온다</item>
/// <item>그 날 계획이 여러 종류면 <b>임의로 고르지 않고 충돌</b>로 둔다.
///       계획에는 교시가 없어 어느 칸이 무엇인지 알 수 없다(D-008)</item>
/// <item>원본과 계획이 어긋나도 충돌 — 조용히 한쪽을 택하지 않는다</item>
/// </list>
/// </summary>
public static class CreativeActivityLinker
{
    /// <summary>창체 칸들을 계획과 이어 붙인다. 창체가 아닌 수업은 결과에 넣지 않는다.</summary>
    public static IReadOnlyList<CreativeLink> Link(
        IEnumerable<TimetableSourceLesson> lessons,
        IEnumerable<MergedCreativeActivity> plan)
    {
        // 날짜별 계획 — 병합이 끝난 것을 받는다(같은 활동이 두 번 세어지면 안 된다, D-009)
        var byDate = plan
            .GroupBy(m => m.Representative.Date)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Representative).ToList());

        var links = new List<CreativeLink>();

        foreach (var lesson in lessons)
        {
            var token = TimetableTokenNormalizer.Normalize(lesson.SourceToken);
            if (!token.IsCreative) continue;   // 일반 과목은 여기서 다루지 않는다

            byDate.TryGetValue(lesson.Cell.Date, out var sameDay);
            links.Add(Resolve(lesson, token, sameDay ?? new List<CreativeActivityEvent>()));
        }

        return links;
    }

    /// <summary>
    /// 두 문서가 서로 맞는 짝인지 확인한다. 학년도·학기가 다르면 연결이 거의 되지 않는다 —
    /// 실제로 다른 학기 파일을 함께 넣어 창체 활동명이 하나도 안 붙는 일을 겪었다.
    /// </summary>
    public static IReadOnlyList<string> CheckPair(
        TimetableSourcePackage timetable, CreativeSourcePackage creative)
    {
        var problems = new List<string>();

        if (timetable.SchoolYear > 0 && creative.SchoolYear > 0
            && timetable.SchoolYear != creative.SchoolYear)
            problems.Add($"학년도가 다릅니다 — 시간표 {timetable.SchoolYear}, 창체 {creative.SchoolYear}.");

        if (timetable.Semester > 0 && creative.Semester > 0
            && timetable.Semester != creative.Semester)
            problems.Add($"학기가 다릅니다 — 시간표 {timetable.Semester}학기, 창체 {creative.Semester}학기.");

        if (timetable.Lessons.Count > 0 && creative.Events.Count > 0)
        {
            var from = timetable.Lessons.Min(l => l.Cell.Date);
            var to = timetable.Lessons.Max(l => l.Cell.Date);
            var outside = creative.Events.Count(e => e.Date < from || e.Date > to);

            if (outside == creative.Events.Count)
                problems.Add($"창체 일정이 모두 시간표 기간({from:yyyy-MM-dd}~{to:MM-dd}) 밖입니다.");
            else if (outside > 0)
                problems.Add($"창체 일정 {outside}건이 시간표 기간 밖입니다.");
        }

        return problems;
    }

    private static CreativeLink Resolve(
        TimetableSourceLesson lesson, TimetableToken token, List<CreativeActivityEvent> sameDay)
    {
        var kinds = sameDay.Select(e => e.Kind)
                           .Where(k => k != CreativeActivityKind.Unresolved)
                           .Distinct().ToList();

        // ① 원본에 종류가 적혀 있다 (자·동·진)
        if (token.CreativeKind != CreativeActivityKind.Unresolved)
        {
            var matched = sameDay.FirstOrDefault(e => e.Kind == token.CreativeKind);

            if (kinds.Count > 0 && !kinds.Contains(token.CreativeKind))
                return new(lesson.Cell, lesson.SourceToken, token.CreativeKind, null,
                    CreativeLinkStatus.Conflict,
                    $"원본은 {Korean(token.CreativeKind)}인데 그 날 계획에는 " +
                    $"{string.Join("·", kinds.Select(Korean))}만 있습니다.");

            return new(lesson.Cell, lesson.SourceToken, token.CreativeKind,
                matched?.ActivityName, CreativeLinkStatus.FromSource,
                matched is null
                    ? $"원본 표기 '{lesson.SourceToken}' → {Korean(token.CreativeKind)}"
                    : $"원본 표기 '{lesson.SourceToken}' → {Korean(token.CreativeKind)} · 계획: {matched.ActivityName}");
        }

        // ② 미분류(창·봉) — 그 날 계획에서 가져온다
        if (kinds.Count == 1)
        {
            var e = sameDay.First(x => x.Kind == kinds[0]);
            return new(lesson.Cell, lesson.SourceToken, kinds[0], e.ActivityName,
                CreativeLinkStatus.FromPlan, $"{e.Date:MM-dd} 계획: {e.ActivityName} ({Korean(kinds[0])})");
        }

        if (kinds.Count > 1)
            return new(lesson.Cell, lesson.SourceToken, CreativeActivityKind.Unresolved, null,
                CreativeLinkStatus.Conflict,
                $"그 날 계획에 {string.Join("·", kinds.Select(Korean))} 이 함께 있어 " +
                "어느 것인지 정할 수 없습니다 (계획에는 교시가 없습니다).");

        return new(lesson.Cell, lesson.SourceToken, CreativeActivityKind.Unresolved, null,
            CreativeLinkStatus.Unresolved,
            sameDay.Count > 0
                ? "그 날 계획의 영역이 비어 있어 종류를 정하지 못했습니다."
                : "그 날 창체 계획이 없습니다.");
    }

    private static string Korean(CreativeActivityKind kind) => kind switch
    {
        CreativeActivityKind.Autonomy => "자율·자치활동",
        CreativeActivityKind.Club => "동아리활동",
        CreativeActivityKind.Career => "진로활동",
        _ => "미분류",
    };
}
