namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 원본 시간표의 과목 표기 하나 (기술설계 §7).
/// </summary>
/// <param name="Raw">문서에 적힌 그대로 — 사용자에게 보여 주고 매핑 키로도 쓴다</param>
/// <param name="Standard">표준 의미(국→국어). 모르는 과목은 정리한 원문 그대로</param>
/// <param name="IsCreativeUnresolved">창·창체·(창) — 자율/동아리/진로 중 무엇인지 아직 모름</param>
/// <param name="IsKnownAlias">사전에 있는 표기였는지. false 여도 버리지 않는다(학교 특색과목)</param>
public sealed record TimetableToken(
    string Raw,
    string Standard,
    bool IsCreativeUnresolved = false,
    bool IsKnownAlias = false);

/// <summary>
/// 원본 과목 표기를 <b>표준 의미까지만</b> 확장한다 (기술설계 §7).
///
/// 여기서 나이스 과목·교사로 바로 바꾸지 않는다 — 실제 항목 선택은 런타임 카탈로그와
/// 사용자 확정의 몫이다(D-002). 사전에 없는 표기도 버리지 않고 그대로 매핑 대상으로 남긴다.
/// </summary>
public static class TimetableTokenNormalizer
{
    /// <summary>창의적 체험활동의 표준 의미 — 세부 종류는 아직 정해지지 않은 상태다(D-008).</summary>
    public const string CreativeStandard = "창의적 체험활동";

    /// <summary>한 글자 별칭과 정식 명칭 → 표준 의미. 비교는 정규화된 형태로 한다.</summary>
    private static readonly (string Alias, string Standard)[] Aliases =
    {
        ("국", "국어"), ("국어", "국어"),
        ("수", "수학"), ("수학", "수학"),
        ("사", "사회"), ("사회", "사회"),
        ("과", "과학"), ("과학", "과학"),
        ("도", "도덕"), ("도덕", "도덕"),
        ("실", "실과"), ("실과", "실과"),
        ("체", "체육"), ("체육", "체육"),
        ("음", "음악"), ("음악", "음악"),
        ("미", "미술"), ("미술", "미술"),
        ("영", "영어"), ("영어", "영어"),
    };

    /// <summary>창의적 체험활동을 가리키는 표기들.</summary>
    private static readonly string[] CreativeTokens = { "창", "창체", "창의적 체험활동", "창의적체험활동" };

    /// <summary>원본 표기 하나를 표준 의미로 확장한다. 빈 값이면 빈 토큰.</summary>
    public static TimetableToken Normalize(string? rawToken)
    {
        var raw = (rawToken ?? string.Empty).Trim();
        if (raw.Length == 0) return new TimetableToken("", "");

        // "(창)" 처럼 감싼 괄호는 벗기고 비교하되, Raw 는 원문을 유지한다
        var bare = TimetableTextNormalizer.Trim(raw);
        var norm = TimetableTextNormalizer.Normalize(bare);

        foreach (var token in CreativeTokens)
            if (norm == TimetableTextNormalizer.Normalize(token))
                return new TimetableToken(raw, CreativeStandard,
                    IsCreativeUnresolved: true, IsKnownAlias: true);

        foreach (var (alias, standard) in Aliases)
            if (norm == TimetableTextNormalizer.Normalize(alias))
                return new TimetableToken(raw, standard, IsKnownAlias: true);

        // 사전에 없는 과목(학교 특색과목 등)은 그대로 보존 — 버리면 매핑할 기회 자체가 사라진다
        return new TimetableToken(raw, bare);
    }

    /// <summary>여러 표기를 한 번에. 원본 순서를 유지한다.</summary>
    public static IReadOnlyList<TimetableToken> NormalizeAll(IEnumerable<string> rawTokens) =>
        rawTokens.Select(Normalize).ToList();
}
