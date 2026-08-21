namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 평가계획 표에서 <b>어느 열이 무엇인지</b>. 머리글을 읽어 정한다.
///
/// <b>열 번호를 못 박지 않는다.</b> 이지에듀와 스쿨마스터가 열 구성이 다르고,
/// 같은 양식이라도 학교마다 열을 더하거나 뺀다 — 시간표에서 요일 칸 수를 못 박았다가
/// 겪은 것과 같은 함정이다(실기기검증 §4-M).
/// </summary>
/// <param name="Area">영역명 열. 없으면 -1</param>
/// <param name="Standard">성취기준 열</param>
/// <param name="Element">평가요소 열. 평가방법과 한 칸을 쓰기도 한다</param>
/// <param name="Level">평가단계 열 (잘함·보통·노력요함)</param>
/// <param name="Result">평가기준(평가결과) 열</param>
public sealed record EvalTableColumns(int Area, int Standard, int Element, int Level, int Result)
{
    /// <summary>넣는 데 꼭 필요한 열이 다 있는가. 평가단계 열은 없을 수도 있다(§3).</summary>
    public bool IsUsable => Standard >= 0 && Result >= 0;

    /// <summary>
    /// 평가기준 열의 머리글 — <b>양식마다 이름이 다르다</b>(실측 2026-08-21).
    /// 이지에듀는 <c>평가기준</c>, 같은 학교 다음 학기 문서는 <c>성취수준</c>, 스쿨마스터는 <c>평가 기준</c>.
    /// </summary>
    private static readonly string[] ResultWords = { "평가기준", "성취수준", "평가결과", "성취기준별평가기준" };

    /// <summary>
    /// 머리글 줄에서 열 위치를 찾는다.
    ///
    /// <b>평가단계 열은 머리글이 비어 있다</b>(실측: 이지에듀는 평가기준 열 머리글이 두 칸에 걸쳐 있고
    /// 왼쪽 칸은 빈 채로 단계 이름만 들어간다). 그래서 평가기준 열 <b>바로 왼쪽</b>을 단계 열로 본다.
    /// </summary>
    public static EvalTableColumns Detect(IReadOnlyList<string> header)
    {
        int Find(params string[] words)
        {
            for (var i = 0; i < header.Count; i++)
            {
                var h = header[i].Replace(" ", "");
                if (words.Any(w => h.Contains(w))) return i;
            }

            return -1;
        }

        var result = Find(ResultWords);
        var element = Find("평가요소");
        var level = result > 0 && header[result - 1].Trim().Length == 0 ? result - 1 : -1;

        return new EvalTableColumns(
            Area: Find("영역"),
            Standard: Find("성취기준"),
            Element: element,
            Level: level,
            Result: result);
    }

    public override string ToString() =>
        $"영역{Area} 성취기준{Standard} 평가요소{Element} 단계{Level} 결과{Result}";
}
