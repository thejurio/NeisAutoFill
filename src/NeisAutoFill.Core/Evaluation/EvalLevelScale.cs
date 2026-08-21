namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 평가 단계 수 판정 (기술설계 E-002).
///
/// <b>문서에는 "3단계"라고 적혀 있지 않다.</b> 평가기준이 <c>잘함·보통·노력요함</c> 처럼
/// 말로만 나열돼 있으므로 <b>줄 수를 세어</b> 정한다.
/// 단계 이름은 학교마다 다르니(상·중·하, 매우잘함…) <b>이름을 고정하지 않는다.</b>
///
/// 나이스 [단계선택] 콤보가 받는 값은 실측상 <c>2·3·4·5·7단계</c> 뿐이다(화면 지도 §4).
/// 그 밖의 수가 나오면 <b>넣지 않고 사람에게 알린다</b> — 조용히 가까운 값으로 맞추지 않는다.
/// </summary>
public static class EvalLevelScale
{
    /// <summary>나이스가 받아 주는 단계 수 (실측 2026-08-21).</summary>
    public static IReadOnlyList<int> Allowed { get; } = new[] { 2, 3, 4, 5, 7 };

    /// <summary>나이스 콤보에 보이는 글자. <c>3</c> → <c>"3단계"</c>.</summary>
    public static string Label(int count) => $"{count}단계";

    public static bool IsAllowed(int count) => Allowed.Contains(count);

    /// <summary>
    /// 단계 수를 판정한다. 받아 주지 않는 수면 <paramref name="reason"/> 에 이유를 담고 null.
    /// </summary>
    public static int? Resolve(int criterionCount, out string? reason)
    {
        if (criterionCount == 0)
        {
            reason = "평가기준이 하나도 없습니다.";
            return null;
        }

        if (!IsAllowed(criterionCount))
        {
            reason = $"평가기준이 {criterionCount}줄인데 나이스는 " +
                     $"{string.Join("·", Allowed)}단계만 받습니다.";
            return null;
        }

        reason = null;
        return criterionCount;
    }
}
