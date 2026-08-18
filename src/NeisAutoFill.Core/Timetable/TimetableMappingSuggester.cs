namespace NeisAutoFill.Core.Timetable;

/// <summary>원본 표기와 나이스 항목이 얼마나 확실하게 맞는지 (기술설계 §7 자동 제안 기준).</summary>
public enum MatchConfidence
{
    /// <summary>맞는 후보가 없음.</summary>
    None,
    /// <summary>비슷하지만 확실하지 않음 — 사람이 봐야 한다.</summary>
    Similar,
    /// <summary>공백·가운뎃점 차이만 있음.</summary>
    Normalized,
    /// <summary>표준 의미가 일치 (국 → 국어).</summary>
    Standard,
    /// <summary>원문이 그대로 일치.</summary>
    Exact,
}

/// <summary>
/// 원본 표기 하나에 대한 제안 (기술설계 §11).
/// <b>제안은 확정이 아니다</b> — 화면에서 "추천됨"과 "사용자 확정"을 구분해 보여 준다.
/// </summary>
/// <param name="Token">원본 표기</param>
/// <param name="Candidates">가능한 나이스 항목들. 여럿이면 사용자가 골라야 한다</param>
/// <param name="Confidence">가장 강한 일치 근거</param>
public sealed record MappingSuggestion(
    TimetableToken Token,
    IReadOnlyList<NeisTimetableOption> Candidates,
    MatchConfidence Confidence)
{
    /// <summary>
    /// 사용자 확인 없이 확정해도 되는가.
    /// 후보가 <b>정확히 하나</b>이고 근거가 정규화 이상일 때만 — 유사도 추천과 창체는 항상 사람이 정한다(§7, D-008).
    /// </summary>
    public bool CanAutoConfirm =>
        Candidates.Count == 1
        && Confidence >= MatchConfidence.Normalized
        && !Token.IsCreativeUnresolved;

    /// <summary>왜 이 후보들이 나왔는지 한 줄 설명.</summary>
    public string Describe()
    {
        // 창은 "비슷한 이름"이 아니라 "종류를 아직 못 정했다"가 정확한 상태다(D-008)
        if (Token.IsCreativeUnresolved)
            return Candidates.Count > 0
                ? "창체 종류(자율·자치/동아리/진로)를 골라 주세요"
                : "창체 항목이 나이스 목록에 없습니다";

        return Confidence switch
        {
            MatchConfidence.Exact => "원문 일치",
            MatchConfidence.Standard => $"표준 의미({Token.Standard}) 일치",
            MatchConfidence.Normalized => "표기 차이만 있음",
            MatchConfidence.Similar => "비슷한 이름 — 확인 필요",
            _ => "맞는 항목이 없습니다",
        };
    }
}

/// <summary>
/// 원본 표기에 붙일 나이스 항목 후보를 찾아 제안한다 (기술설계 §7·§11).
///
/// 순서: 원문 정확 → 표준 의미 → 정규화 → 유사도. 앞에서 찾으면 뒤는 보지 않는다.
/// 같은 과목에 교사가 여럿이면 <b>전부 후보로 남긴다</b> — 하나를 고르는 것은 사용자의 몫이다(D-006).
/// </summary>
public static class TimetableMappingSuggester
{
    public static MappingSuggestion Suggest(TimetableToken token, TimetableCatalog catalog)
    {
        // 창체는 창체 항목만 후보로 — 일반 과목에 붙이면 안 된다(D-008)
        if (token.IsCreative)
        {
            var creative = catalog.Assignable
                .Where(o => o.Kind == TimetableOptionKind.CreativeActivity).ToList();

            // 원본에 종류가 적혀 있으면(자·동·진) 그 종류만 후보로 좁힌다 — 교사는 여전히 사용자가 고른다
            if (token.CreativeKind != CreativeActivityKind.Unresolved)
            {
                var narrowed = creative.Where(o => o.CreativeKind == token.CreativeKind).ToList();
                if (narrowed.Count > 0)
                    return new MappingSuggestion(token, narrowed, MatchConfidence.Standard);
            }

            return new MappingSuggestion(token, creative,
                creative.Count > 0 ? MatchConfidence.Similar : MatchConfidence.None);
        }

        var lessons = catalog.Assignable.Where(o => o.Kind == TimetableOptionKind.Lesson).ToList();

        // ① 원문 그대로
        var exact = lessons.Where(o => o.Subject == token.Raw).ToList();
        if (exact.Count > 0) return new MappingSuggestion(token, exact, MatchConfidence.Exact);

        // ② 표준 의미
        var standard = lessons.Where(o => o.Subject == token.Standard).ToList();
        if (standard.Count > 0) return new MappingSuggestion(token, standard, MatchConfidence.Standard);

        // ③ 공백·가운뎃점 차이 흡수
        var normStandard = TimetableTextNormalizer.Normalize(token.Standard);
        var normalized = lessons
            .Where(o => TimetableTextNormalizer.Normalize(o.Subject) == normStandard).ToList();
        if (normalized.Count > 0) return new MappingSuggestion(token, normalized, MatchConfidence.Normalized);

        // ④ 유사도 — 포함 관계만 본다(편집거리는 과목명이 짧아 오탐이 많다)
        var similar = lessons.Where(o =>
        {
            var s = TimetableTextNormalizer.Normalize(o.Subject);
            return s.Length > 0 && normStandard.Length > 0
                && (s.Contains(normStandard) || normStandard.Contains(s));
        }).ToList();
        if (similar.Count > 0) return new MappingSuggestion(token, similar, MatchConfidence.Similar);

        return new MappingSuggestion(token, Array.Empty<NeisTimetableOption>(), MatchConfidence.None);
    }

    /// <summary>원본에 나온 표기들을 한 번에 제안한다. 같은 표기는 한 줄로 합친다.</summary>
    public static IReadOnlyList<MappingSuggestion> SuggestAll(
        IEnumerable<TimetableToken> tokens, TimetableCatalog catalog) =>
        tokens
            .GroupBy(t => t.Raw)
            .Select(g => Suggest(g.First(), catalog))
            .ToList();

    /// <summary>
    /// 자동 확정할 수 있는 제안만 규칙으로 바꾼다 (전체 기본값 범위).
    /// 나머지는 사용자가 화면에서 정해야 하므로 규칙을 만들지 않는다.
    /// </summary>
    public static IReadOnlyList<TimetableMappingRule> AutoConfirm(
        IEnumerable<MappingSuggestion> suggestions, string catalogFingerprint) =>
        suggestions
            .Where(s => s.CanAutoConfirm)
            .Select(s => new TimetableMappingRule(
                s.Token.Raw, s.Candidates[0].StableKey, MappingScope.Default,
                IsUserConfirmed: false, CatalogFingerprint: catalogFingerprint))
            .ToList();
}
