namespace NeisAutoFill.Core.Evaluation;

/// <summary>나이스 일괄업로드 엑셀의 한 줄 — 양식이 요구하는 세 칸 그대로.</summary>
public sealed record EvalUploadRow(string Area, string Standard, string Element);

/// <summary>
/// 교과 하나의 계획을 <b>일괄업로드용 줄</b>로 펴고, <b>이미 올라간 것은 뺀다</b>.
///
/// 나이스 안내문 그대로: <c>"기 등록된 영역명, 성취기준은 그대로 유지되고,
/// 업로드한 성취기준이 추가로 등록됩니다"</c> — <b>덮어쓰기가 아니라 추가</b>다(E-004).
/// 그래서 두 번 올리면 두 벌이 된다. 무엇이 이미 있는지는 <b>우리가</b> 가려야 한다.
/// </summary>
public static class EvalUploadRows
{
    /// <summary>계획을 줄로 편다. 영역·성취기준 순서를 그대로 지킨다.</summary>
    public static IReadOnlyList<EvalUploadRow> Of(EvalSubjectPlan plan) =>
        plan.Areas
            .SelectMany(a => a.Standards.Select(s =>
                new EvalUploadRow(a.Name.Trim(), Flat(s.Standard), Flat(s.Element))))
            .Where(r => r.Area.Length > 0 && r.Standard.Length > 0)
            .ToList();

    /// <summary>
    /// 아직 화면에 없는 줄만 남긴다.
    ///
    /// 견주는 기준은 <b>(영역명, 성취기준)</b> 이고, 공백은 무시한다 —
    /// 문서에서 온 문장과 화면에서 읽은 문장은 줄바꿈·띄어쓰기가 곧잘 다르다.
    /// 같은 줄이 우리 목록 안에서 두 번 나와도 한 번만 올린다.
    /// </summary>
    public static IReadOnlyList<EvalUploadRow> Pending(
        IEnumerable<EvalUploadRow> wanted,
        IEnumerable<(string Area, string Standard)> existing)
    {
        var seen = existing.Select(e => Key(e.Area, e.Standard)).ToHashSet();
        var result = new List<EvalUploadRow>();

        foreach (var row in wanted)
            if (seen.Add(Key(row.Area, row.Standard))) result.Add(row);

        return result;
    }

    /// <summary>여러 줄짜리 값을 한 줄로 — 엑셀 한 칸에 들어가야 한다.</summary>
    private static string Flat(string value) =>
        string.Join(" ", value.Split('\n', '\r').Select(p => p.Trim()).Where(p => p.Length > 0));

    private static string Key(string area, string standard) =>
        Squash(area) + " / " + Squash(standard);

    private static string Squash(string value) =>
        new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
