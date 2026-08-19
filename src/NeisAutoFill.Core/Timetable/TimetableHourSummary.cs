namespace NeisAutoFill.Core.Timetable;

/// <summary>과목 한 줄의 시수 비교.</summary>
/// <param name="Subject">과목명 (창체 계열은 모두 "창체" 한 줄로 합친다)</param>
/// <param name="Assigned">시간표에 실제로 배정된 칸 수</param>
/// <param name="Standard">기준 시수. 모르면 null</param>
/// <param name="StandardIsEdited">교사가 직접 고친 기준인지 (문서에서 읽은 값이 아니라)</param>
public sealed record SubjectHourRow(
    string Subject,
    int Assigned,
    int? Standard,
    bool StandardIsEdited = false)
{
    /// <summary>배정 − 기준. 기준을 모르면 null.</summary>
    public int? Difference => Standard is null ? null : Assigned - Standard;

    /// <summary>기준을 맞췄는가. 기준을 모르면 판정하지 않는다.</summary>
    public bool IsBalanced => Difference == 0;

    /// <summary>"+3" · "-2" · "0" · 기준을 모르면 빈 문자열.</summary>
    public string DifferenceText => Difference?.ToString("+#;-#;0") ?? "";
}

/// <summary>
/// 배정 시수와 기준 시수를 나란히 놓는다 (시간표 탭의 시수 표).
///
/// 기준은 두 군데서 온다:
/// <list type="bullet">
/// <item>문서 끝 시수표 (<see cref="TimetableHoursParser"/>)</item>
/// <item>교사가 직접 고친 값 — 문서에 표가 없거나 값이 다를 때</item>
/// </list>
/// 교사가 고친 값이 언제나 우선한다.
/// </summary>
public static class TimetableHourSummary
{
    /// <summary>창의적 체험활동은 자율·동아리·봉사·진로를 <b>한 줄로</b> 합친다 — 시수표도 '창체' 한 칸이다.</summary>
    public const string CreativeRow = "창체";

    /// <param name="lessons">그 학기의 수업 (기간으로 미리 걸러서 넘긴다)</param>
    /// <param name="standards">문서에서 읽은 기준 시수. 없으면 빈 목록</param>
    /// <param name="semester">1 또는 2. 0 이면 본교 기준(연간) 전체를 쓴다</param>
    /// <param name="edits">교사가 고친 기준 (과목 → 시수). 문서 값보다 우선한다</param>
    public static IReadOnlyList<SubjectHourRow> Build(
        IEnumerable<TimetableSourceLesson> lessons,
        IReadOnlyList<SubjectHourStandard> standards,
        int semester,
        IReadOnlyDictionary<string, int>? edits = null)
    {
        var assigned = new Dictionary<string, int>();
        foreach (var lesson in lessons)
        {
            var name = RowNameOf(lesson.SourceToken);
            assigned[name] = assigned.GetValueOrDefault(name) + 1;
        }

        // 문서 기준에만 있고 시간표에는 한 칸도 없는 과목도 보여 준다 — "0시간 배정"이 곧 문제다
        var names = assigned.Keys
            .Concat(standards.Select(s => s.Subject))
            .Concat(edits?.Keys ?? Enumerable.Empty<string>())
            .Distinct()
            .ToList();

        var rows = names.Select(name =>
        {
            var edited = edits is not null && edits.TryGetValue(name, out var e);
            var standard = edited
                ? edits![name]
                : standards.FirstOrDefault(s => s.Subject == name)?.For(semester);

            return new SubjectHourRow(name, assigned.GetValueOrDefault(name), standard, edited);
        });

        // 배정이 많은 과목부터. 기준만 있고 배정이 없는 과목은 뒤로 밀리며 눈에 띈다
        return rows.OrderByDescending(r => r.Assigned).ThenBy(r => r.Subject, StringComparer.Ordinal).ToList();
    }

    /// <summary>이 표기가 시수 표의 어느 줄에 들어가는지.</summary>
    public static string RowNameOf(string sourceToken)
    {
        var token = TimetableTokenNormalizer.Normalize(sourceToken);
        return token.IsCreative ? CreativeRow : token.Standard;
    }
}
