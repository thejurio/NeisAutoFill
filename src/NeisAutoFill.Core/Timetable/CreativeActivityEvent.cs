namespace NeisAutoFill.Core.Timetable;

/// <summary>창체 일정이 어느 문서에서 왔는지. 같은 활동이 두 곳에 적히는 일이 흔하다.</summary>
public enum CreativeSourceKind
{
    /// <summary>연간 창체 전체 계획.</summary>
    Overall,
    /// <summary>영역별 세부 계획 — 더 구체적이라 병합 시 대표로 삼는다.</summary>
    Detail,
}

/// <summary>
/// 창의적 체험활동 일정 한 건 (기술설계 §5 CreativeActivityEvent).
/// 원본 문서에서 읽은 그대로이며, 나이스 항목으로 확정하는 것은 매핑 단계의 몫이다(D-002).
/// </summary>
/// <param name="Date">실제 날짜</param>
/// <param name="Period">교시 (계획에 없으면 null — 날짜만 아는 경우가 많다)</param>
/// <param name="Kind">자율·자치/동아리/진로. 알 수 없으면 Unresolved(D-008)</param>
/// <param name="ActivityName">활동명</param>
/// <param name="Source">출처 문서 종류</param>
/// <param name="SourceLocation">원본 위치 추적 정보 (p.2/table-1/r4 등)</param>
public sealed record CreativeActivityEvent(
    DateOnly Date,
    int? Period,
    CreativeActivityKind Kind,
    string ActivityName,
    CreativeSourceKind Source,
    string SourceLocation = "")
{
    /// <summary>날짜+교시+종류+활동명이 모두 같을 때의 키 — 자동 병합 판단에 쓴다.</summary>
    public string ExactKey =>
        $"{Date:yyyyMMdd}|{Period?.ToString() ?? "-"}|{Kind}|{TimetableTextNormalizer.Normalize(ActivityName)}";

    /// <summary>교시를 뺀 키 — 자동 병합이 아니라 <b>병합 후보 제안</b>에만 쓴다(기술설계 §8).</summary>
    public string LooseKey =>
        $"{Date:yyyyMMdd}|{Kind}|{TimetableTextNormalizer.Normalize(ActivityName)}";
}

/// <summary>병합 결과 한 건. 대표 일정 하나에 출처들이 붙는다.</summary>
/// <param name="Representative">대표 일정 (세부 계획이 있으면 그쪽)</param>
/// <param name="Sources">병합된 원본들 — 삭제하지 않고 모두 남긴다(기술설계 §8)</param>
public sealed record MergedCreativeActivity(
    CreativeActivityEvent Representative,
    IReadOnlyList<CreativeActivityEvent> Sources)
{
    public bool WasMerged => Sources.Count > 1;

    /// <summary>왜 하나로 묶였는지 — 화면에서 확인할 수 있어야 한다(T3 완료 기준).</summary>
    public string Describe() => WasMerged
        ? $"{Sources.Count}건 병합 (대표: {Representative.Source})"
        : "단일 일정";
}

/// <summary>자동으로 묶지 않고 사용자에게 제안만 하는 병합 후보.</summary>
/// <param name="Reason">왜 후보인지 (교시 정보 차이 등)</param>
public sealed record CreativeMergeSuggestion(
    IReadOnlyList<CreativeActivityEvent> Candidates,
    string Reason);

/// <summary>사람이 풀어야 하는 충돌. 하나를 임의로 고르지 않는다(기술설계 §8).</summary>
public sealed record CreativeMergeConflict(
    DateOnly Date,
    IReadOnlyList<CreativeActivityEvent> Events,
    string Reason);

/// <summary>병합 전체 결과.</summary>
public sealed record CreativeMergeResult(
    IReadOnlyList<MergedCreativeActivity> Merged,
    IReadOnlyList<CreativeMergeSuggestion> Suggestions,
    IReadOnlyList<CreativeMergeConflict> Conflicts)
{
    /// <summary>충돌이 하나라도 있으면 실행을 막는다 (T3 완료 기준).</summary>
    public bool HasBlockingIssue => Conflicts.Count > 0;
}
