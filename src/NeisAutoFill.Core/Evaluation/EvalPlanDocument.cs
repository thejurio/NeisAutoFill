namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 평가기준 한 줄 — 단계 이름과 그 단계에 해당하는 문장 (기술설계 §4).
/// </summary>
/// <param name="Level">단계 이름. <b>학교마다 다르다</b> — "잘함/보통/노력요함", "상/중/하" 등</param>
/// <param name="Result">그 단계의 평가결과 문장</param>
public sealed record EvalCriterion(string Level, string Result);

/// <summary>
/// 성취기준 하나와 그에 딸린 평가기준들.
/// </summary>
/// <param name="Standard">성취기준 문장</param>
/// <param name="Element">평가요소</param>
/// <param name="Criteria">단계별 평가기준. 순서가 곧 화면 입력 순서다(위가 높은 단계)</param>
/// <param name="Code">문서에 적힌 성취기준 코드 (없으면 빈 문자열) — 나이스에는 넣지 않는다</param>
/// <param name="SpansPages">
/// 쪽을 넘어 <b>이어붙인</b> 것인가. 원문 한 쪽 안에 통째로 들어 있지 않으므로
/// 기계 대조(원문 부분문자열 확인)로는 맞는지 알 수 없다 — 사람이 봐야 한다.
/// </param>
public sealed record EvalStandard(
    string Standard,
    string Element,
    IReadOnlyList<EvalCriterion> Criteria,
    string Code = "",
    bool SpansPages = false)
{
    /// <summary>단계 수. 문서에 숫자로 적혀 있지 않으므로 <b>세어서</b> 정한다(E-002).</summary>
    public int LevelCount => Criteria.Count;
}

/// <summary>영역 하나. 영역명은 나이스에 <b>미리 등록</b>해야 고를 수 있다(E-001).</summary>
public sealed record EvalArea(string Name, IReadOnlyList<EvalStandard> Standards);

/// <summary>교과 하나. 나이스의 일괄업로드는 이 단위로 동작한다.</summary>
public sealed record EvalSubjectPlan(string Subject, IReadOnlyList<EvalArea> Areas)
{
    public int StandardCount => Areas.Sum(a => a.Standards.Count);
}

/// <summary>
/// 평가계획 문서 한 벌 (기술설계 §4).
/// 문서에서 읽어낸 것만 담는다 — 나이스 사정은 모른다.
/// </summary>
/// <param name="Subjects">교과별 계획</param>
/// <param name="SchoolYear">문서에 적힌 학년도 (못 읽으면 null)</param>
/// <param name="Semester">학기 (못 읽으면 null)</param>
/// <param name="Grade">학년 (못 읽으면 null)</param>
/// <param name="Ignored">
/// 교과를 가리지 못해 <b>뺀</b> 영역들 (중복 없이).
///
/// 성취기준 코드(<c>[6국04-05]</c>)가 없으면 어느 교과인지 알 길이 없다.
/// 그런 것은 <b>창의적 체험활동</b>(자율·동아리·진로)과 <b>학교자율시간</b>, 그리고
/// 표에 섞여 든 <b>쪽 꼬리말</b>이다. 나이스 평가계획은 교과(목) 단위라 넣을 자리가 없다.
/// 사용자가 "아예 무시해 달라"고 정했다(2026-08-21) — <b>다만 몇 건을 뺐는지는 알린다.</b>
/// </param>
public sealed record EvalPlanDocument(
    IReadOnlyList<EvalSubjectPlan> Subjects,
    int? SchoolYear = null,
    int? Semester = null,
    int? Grade = null,
    IReadOnlyList<string>? Ignored = null)
{
    /// <summary>뺀 영역들 (없으면 빈 목록).</summary>
    public IReadOnlyList<string> Skipped => Ignored ?? Array.Empty<string>();

    public int AreaCount => Subjects.Sum(s => s.Areas.Count);

    public int StandardCount => Subjects.Sum(s => s.StandardCount);

    /// <summary>사람이 검산할 수 있게 한 줄로 — 교과 수·영역 수·성취기준 수.</summary>
    public string Describe() =>
        $"교과 {Subjects.Count} · 영역 {AreaCount} · 성취기준 {StandardCount}" +
        (Skipped.Count > 0 ? $" · 뺀 영역 {Skipped.Count}" : "");
}
