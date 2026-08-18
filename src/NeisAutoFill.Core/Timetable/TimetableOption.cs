namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 나이스 시간표 셀 우클릭 메뉴 항목의 종류.
/// 자동 입력 후보가 될 수 있는 것은 <see cref="Lesson"/>·<see cref="CreativeActivity"/> 뿐이다 (기술설계 §5·§10).
/// </summary>
public enum TimetableOptionKind
{
    /// <summary>일반 수업 — 과목(교사명(계정ID)).</summary>
    Lesson,
    /// <summary>창의적 체험활동 — 자율·자치/동아리/진로.</summary>
    CreativeActivity,
    /// <summary>행사처리·수업삭제 같은 작업 명령. <b>절대 수업 후보로 쓰지 않는다</b>(불변조건 7).</summary>
    Command,
    /// <summary>메뉴 닫기용 [취소].</summary>
    Cancel,
    /// <summary>구조를 해석하지 못한 항목. 후보에서 제외하고 사용자에게 보고한다.</summary>
    Unknown,
}

/// <summary>창의적 체험활동 세부 종류. 원본의 "창"만으로는 알 수 없으므로 기본은 <see cref="Unresolved"/>(D-008).</summary>
public enum CreativeActivityKind
{
    /// <summary>아직 무엇인지 정해지지 않음 — 이 상태가 하나라도 남으면 실행을 막는다.</summary>
    Unresolved,
    /// <summary>자율·자치활동.</summary>
    Autonomy,
    /// <summary>동아리활동.</summary>
    Club,
    /// <summary>진로활동.</summary>
    Career,
}

/// <summary>
/// 나이스 메뉴에서 실제로 읽어낸 항목 하나 (기술설계 §5 NeisTimetableOption).
/// 과목·교사 목록은 학교·계정마다 다르므로 <b>런타임에 읽은 이 값이 최종 권위</b>다(D-001).
/// </summary>
/// <param name="Kind">항목 종류</param>
/// <param name="RawText">화면 원문 — 진단·보고용(개인정보 포함 가능, 로그에는 마스킹)</param>
/// <param name="Subject">과목명 또는 창체 활동명</param>
/// <param name="TeacherName">교사 표시명 (없으면 빈 문자열)</param>
/// <param name="TeacherAccount">교사 계정 식별자 (없으면 빈 문자열) — 동명이인 구별용</param>
/// <param name="CreativeKind">창체일 때 세부 종류</param>
public sealed record NeisTimetableOption(
    TimetableOptionKind Kind,
    string RawText,
    string Subject = "",
    string TeacherName = "",
    string TeacherAccount = "",
    CreativeActivityKind CreativeKind = CreativeActivityKind.Unresolved)
{
    /// <summary>자동 입력 대상이 될 수 있는 항목인가. 명령·취소·해석 실패는 제외.</summary>
    public bool IsAssignable => Kind is TimetableOptionKind.Lesson or TimetableOptionKind.CreativeActivity;

    /// <summary>
    /// 카탈로그 안에서 이 항목을 가리키는 안정 키.
    /// 화면 uuid 는 메뉴를 다시 열 때마다 바뀌므로 절대 쓰지 않는다(D-004).
    /// 종류·과목·교사·계정을 정규화해 만든다 — 메뉴 순서가 바뀌어도 같은 키가 나온다.
    ///
    /// <b>교사명·계정은 해시로 넣는다</b>(D-012) — 이 키는 매핑 프로필 파일과 진단 리포트에 그대로
    /// 저장되므로 원문이 남으면 개인정보가 파일에 새어 나간다. 과목명은 개인정보가 아니라 그대로 둔다.
    /// 카탈로그와 규칙이 같은 방식으로 키를 만들기 때문에 대조에는 문제가 없다.
    /// </summary>
    public string StableKey => Kind switch
    {
        TimetableOptionKind.Lesson =>
            $"L|{Norm(Subject)}|{Person(TeacherName)}|{Person(TeacherAccount)}",
        TimetableOptionKind.CreativeActivity =>
            $"C|{CreativeKind}|{Norm(Subject)}|{Person(TeacherName)}|{Person(TeacherAccount)}",
        TimetableOptionKind.Command => $"X|{Norm(RawText)}",
        TimetableOptionKind.Cancel => "X|취소",
        _ => $"?|{Norm(RawText)}",
    };

    private static string Norm(string s) => TimetableTextNormalizer.Normalize(s);

    /// <summary>사람을 가리키는 값은 짧은 해시로 — 같은 사람은 항상 같은 값이라 대조는 그대로 된다.</summary>
    private static string Person(string s)
    {
        var norm = Norm(s);
        if (norm.Length == 0) return "";
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(norm));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
