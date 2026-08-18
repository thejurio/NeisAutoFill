namespace NeisAutoFill.Core.Timetable;

/// <summary>매핑 해석 결과의 종류.</summary>
public enum MappingResolutionKind
{
    /// <summary>대상이 하나로 정해짐.</summary>
    Resolved,
    /// <summary>사용자가 "입력 안 함"으로 정함 — 미해결과 구분한다.</summary>
    Skip,
    /// <summary>맞는 규칙이 없음 — 아직 정하지 않았다.</summary>
    Unresolved,
    /// <summary>같은 우선순위에서 서로 다른 대상이 둘 이상 — 임의 선택 금지(D-007).</summary>
    Conflict,
}

/// <summary>
/// 셀 하나에 대한 매핑 해석 결과.
/// <see cref="AppliedRule"/> 를 항상 함께 돌려주어 "왜 이 대상이 선택됐는지" 설명할 수 있게 한다.
/// </summary>
public sealed record MappingResolution(
    MappingResolutionKind Kind,
    string TargetStableKey = "",
    TimetableMappingRule? AppliedRule = null,
    IReadOnlyList<TimetableMappingRule>? ConflictingRules = null)
{
    /// <summary>선택 이유 한 줄 — 미리보기·결과 대시보드에 그대로 쓴다.</summary>
    public string Describe() => Kind switch
    {
        MappingResolutionKind.Resolved => $"{AppliedRule!.Scope.Describe()} 규칙",
        MappingResolutionKind.Skip => $"{AppliedRule!.Scope.Describe()} — 입력 안 함",
        MappingResolutionKind.Unresolved => "매핑 미해결",
        MappingResolutionKind.Conflict =>
            $"같은 범위({ConflictingRules![0].Scope.Describe()})에 서로 다른 규칙 {ConflictingRules!.Count}개",
        _ => "알 수 없음",
    };
}

/// <summary>
/// 원본 표기 + 셀 → 나이스 대상 항목을 정한다 (기술설계 §5, D-007).
///
/// 우선순위: <b>날짜+교시 &gt; 요일+교시 &gt; 요일 &gt; 전체 기본값</b>.
/// 가장 구체적인 범위의 규칙만 남기고, 그 안에서 대상이 갈리면 첫 항목을 고르지 않고 충돌로 돌려준다.
/// </summary>
public static class TimetableMappingResolver
{
    /// <summary>셀 하나를 해석한다.</summary>
    public static MappingResolution Resolve(
        string sourceToken, TimetableCell cell, IEnumerable<TimetableMappingRule> rules)
    {
        var token = TimetableTextNormalizer.Normalize(sourceToken);

        var candidates = rules
            .Where(r => TimetableTextNormalizer.Normalize(r.SourceToken) == token && r.Scope.Matches(cell))
            .ToList();

        if (candidates.Count == 0) return new MappingResolution(MappingResolutionKind.Unresolved);

        // 가장 구체적인 범위만 남긴다 — 덜 구체적인 규칙은 덮인다
        var top = candidates.Max(r => r.Scope.Priority);
        var winners = candidates.Where(r => r.Scope.Priority == top).ToList();

        // 같은 우선순위에서 대상이 갈리면 사람이 정해야 한다.
        // (대상이 같은 중복 규칙은 모호하지 않으므로 충돌로 보지 않는다 — 첫 항목을 고르는 것이 아니다)
        var distinctTargets = winners.Select(r => r.TargetStableKey).Distinct().ToList();
        if (distinctTargets.Count > 1)
            return new MappingResolution(MappingResolutionKind.Conflict, ConflictingRules: winners);

        var applied = winners[0];
        return applied.IsSkip
            ? new MappingResolution(MappingResolutionKind.Skip, AppliedRule: applied)
            : new MappingResolution(MappingResolutionKind.Resolved, applied.TargetStableKey, applied);
    }
}
