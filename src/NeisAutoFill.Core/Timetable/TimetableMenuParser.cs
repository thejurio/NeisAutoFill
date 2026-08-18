namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 시간표 셀 우클릭 메뉴의 항목 문자열을 해석한다 (기술설계 §10).
///
/// 형식: <c>과목(교사명(계정ID))</c>
/// 왼쪽 첫 괄호에서 자르면 "보강등록(현장체험학습)" 같은 명령이나 괄호가 든 과목명을 오분류한다.
/// 그래서 ① 알려진 작업 명령을 먼저 걸러내고 ② <b>맨 오른쪽 바깥 괄호</b>부터 균형을 맞춰 벗긴다.
///
/// 해석하지 못한 항목은 버리지 않고 <see cref="TimetableOptionKind.Unknown"/> 으로 남겨
/// 자동 후보에서만 제외한다 — 조용히 사라지면 원인을 알 수 없다.
/// </summary>
public static class TimetableMenuParser
{
    /// <summary>2026-08-18 실측된 작업 명령(실기기검증 §4). 환경마다 다를 수 있으므로 개수를 상수로 쓰지 않는다.</summary>
    private static readonly string[] KnownCommands =
    {
        "결보강처리", "보강등록(현장체험학습)", "행사처리", "공백삽입", "공백삭제",
        "수업삭제", "전체 수업삭제", "교사추가",
    };

    private const string CancelText = "취소";

    /// <summary>창체 활동명 → 세부 종류. 정규화된 형태로 비교한다.</summary>
    private static readonly (string Normalized, CreativeActivityKind Kind)[] CreativeNames =
    {
        (TimetableTextNormalizer.Normalize("자율·자치활동"), CreativeActivityKind.Autonomy),
        (TimetableTextNormalizer.Normalize("동아리활동"),   CreativeActivityKind.Club),
        (TimetableTextNormalizer.Normalize("진로활동"),     CreativeActivityKind.Career),
    };

    /// <summary>메뉴 항목 하나를 해석한다.</summary>
    public static NeisTimetableOption Parse(string? rawText)
    {
        var raw = (rawText ?? string.Empty).Trim();
        if (raw.Length == 0) return new NeisTimetableOption(TimetableOptionKind.Unknown, raw);

        // 괄호 짝이 안 맞으면 과목·교사 구조를 신뢰할 수 없다 — 추측하지 않고 사용자에게 넘긴다
        if (!TimetableTextNormalizer.HasBalancedParens(raw))
            return new NeisTimetableOption(TimetableOptionKind.Unknown, raw);

        var norm = TimetableTextNormalizer.Normalize(raw);

        // ① 취소·작업 명령 먼저 — 수업 후보로 새어 나가면 안 된다 (불변조건 7)
        if (norm == TimetableTextNormalizer.Normalize(CancelText))
            return new NeisTimetableOption(TimetableOptionKind.Cancel, raw);

        foreach (var cmd in KnownCommands)
            if (norm == TimetableTextNormalizer.Normalize(cmd))
                return new NeisTimetableOption(TimetableOptionKind.Command, raw);

        // ② 과목(교사(계정)) 구조 벗기기 — 오른쪽부터
        var (subject, teacher, account) = SplitSubjectTeacherAccount(raw);
        if (subject.Length == 0) return new NeisTimetableOption(TimetableOptionKind.Unknown, raw);

        // ③ 창체 여부는 과목명(=활동명)으로 판정
        var subjectNorm = TimetableTextNormalizer.Normalize(subject);
        foreach (var (name, kind) in CreativeNames)
            if (subjectNorm == name)
                return new NeisTimetableOption(
                    TimetableOptionKind.CreativeActivity, raw, subject, teacher, account, kind);

        return new NeisTimetableOption(TimetableOptionKind.Lesson, raw, subject, teacher, account);
    }

    /// <summary>메뉴 항목 목록을 한 번에 해석한다.</summary>
    public static IReadOnlyList<NeisTimetableOption> ParseAll(IEnumerable<string> rawTexts) =>
        rawTexts.Select(Parse).ToList();

    /// <summary>
    /// "과목(교사명(계정ID))" 를 세 조각으로. 교사·계정이 없으면 빈 문자열.
    /// 맨 오른쪽 바깥 괄호 한 겹을 벗겨 교사 구획을 얻고, 그 안에서 다시 마지막 괄호를 벗겨 계정을 얻는다.
    /// </summary>
    private static (string Subject, string Teacher, string Account) SplitSubjectTeacherAccount(string raw)
    {
        var (head, inner) = PeelLastParen(raw);
        if (inner is null) return (TimetableTextNormalizer.Trim(raw), "", "");   // 괄호 없음 = 과목만

        var (teacher, account) = PeelLastParen(inner);
        return (TimetableTextNormalizer.Trim(head),
                TimetableTextNormalizer.Trim(teacher),
                TimetableTextNormalizer.Trim(account ?? ""));
    }

    /// <summary>
    /// 문자열 끝의 괄호 한 쌍을 균형 있게 벗긴다. 끝이 ')' 가 아니거나 짝이 안 맞으면 (원문, null).
    /// 반환: (괄호 앞부분, 괄호 안쪽) — 중첩 괄호는 안쪽에 그대로 남는다.
    /// </summary>
    private static (string Head, string? Inner) PeelLastParen(string s)
    {
        s = s.TrimEnd();
        if (s.Length < 2 || s[^1] != ')') return (s, null);

        var depth = 0;
        for (var i = s.Length - 1; i >= 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(')
            {
                depth--;
                if (depth == 0) return (s[..i], s[(i + 1)..^1]);
            }
        }
        return (s, null);   // 짝이 안 맞음 — 건드리지 않는다
    }
}
