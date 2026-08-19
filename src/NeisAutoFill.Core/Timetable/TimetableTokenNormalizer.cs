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
    bool IsKnownAlias = false,
    CreativeActivityKind CreativeKind = CreativeActivityKind.Unresolved)
{
    /// <summary>창체 계열인가 (종류가 정해졌든 아니든).</summary>
    public bool IsCreative => IsCreativeUnresolved || CreativeKind != CreativeActivityKind.Unresolved;
}

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

        // 1~2학년 통합교과 — 전국 공통이라 안심하고 넣는다.
        // 3학년부터는 없고, 대신 사회·과학·실과가 학년에 따라 생긴다.
        // <b>학년별로 무엇이 있는지는 하드코딩하지 않는다</b> — 나이스 목록이 최종 권위다(D-001).
        ("바", "바른 생활"), ("바생", "바른 생활"), ("바른생활", "바른 생활"), ("바른 생활", "바른 생활"),
        ("슬", "슬기로운 생활"), ("슬생", "슬기로운 생활"),
        ("슬기로운생활", "슬기로운 생활"), ("슬기로운 생활", "슬기로운 생활"),
        ("즐", "즐거운 생활"), ("즐생", "즐거운 생활"),
        ("즐거운생활", "즐거운 생활"), ("즐거운 생활", "즐거운 생활"),

        // 학교자율시간 — <b>줄임 글자는 학교마다 다르다</b>("융" 등).
        // 정식 이름만 사전에 두고, 학교별 표기는 사용자가 한 번 이어 주면 저장된다(§13).
        ("학교자율시간", "학교자율시간"), ("학교 자율 시간", "학교자율시간"),
        ("학교자율", "학교자율시간"), ("자율시간", "학교자율시간"),
    };

    /// <summary>종류를 알 수 없는 창체 표기.</summary>
    private static readonly string[] CreativeTokens = { "창", "창체", "창의적 체험활동", "창의적체험활동" };

    /// <summary>
    /// 종류까지 적힌 창체 표기 (2026-08-19 실제 시간표에서 확인 — 자·동·진·봉이 모두 쓰인다).
    /// <b>봉사활동은 나이스 메뉴에 없다</b>(자율·자치/동아리/진로 3종뿐) — 미분류로 두고 사용자가 정하게 한다.
    /// </summary>
    private static readonly (string Alias, string Standard, CreativeActivityKind Kind)[] CreativeAliases =
    {
        ("자", "자율·자치활동", CreativeActivityKind.Autonomy),
        ("자율", "자율·자치활동", CreativeActivityKind.Autonomy),
        ("자율·자치활동", "자율·자치활동", CreativeActivityKind.Autonomy),
        ("동", "동아리활동", CreativeActivityKind.Club),
        ("동아리", "동아리활동", CreativeActivityKind.Club),
        ("동아리활동", "동아리활동", CreativeActivityKind.Club),
        ("진", "진로활동", CreativeActivityKind.Career),
        ("진로", "진로활동", CreativeActivityKind.Career),
        ("진로활동", "진로활동", CreativeActivityKind.Career),
        ("봉", "봉사활동", CreativeActivityKind.Unresolved),
        ("봉사", "봉사활동", CreativeActivityKind.Unresolved),
    };

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

        foreach (var (alias, standard, kind) in CreativeAliases)
            if (norm == TimetableTextNormalizer.Normalize(alias))
                return new TimetableToken(raw, standard,
                    IsCreativeUnresolved: kind == CreativeActivityKind.Unresolved,
                    IsKnownAlias: true, CreativeKind: kind);

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
