using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Core.Matching;

/// <summary>
/// 학생의 영역별 등급(2단계 엑셀) + 평가계획(1단계 엑셀)을 결합해
/// AI 생성 요청에 넘길 DomainPoint 목록을 만든다. (Index.html processStep2File 의 결합부와 동형)
/// </summary>
public static class EvaluationAssembler
{
    public const string NoCriteriaText = "[기준 없음]";

    public static IReadOnlyList<DomainPoint> BuildDomainPoints(
        Student student,
        IReadOnlyList<string> areas,
        SubjectPlan? plan,
        GradeScale scale)
    {
        var points = new List<DomainPoint>();
        foreach (var area in areas)
        {
            if (!student.Grades.TryGetValue(area, out var grade)) continue;
            grade = grade.Trim();
            if (!scale.Contains(grade)) continue;   // 척도 밖 값은 생성에서도 제외

            CriteriaEntry entry = plan is not null &&
                plan.Criteria.TryGetValue((area, grade), out var e)
                    ? e
                    : new CriteriaEntry(NoCriteriaText, null);

            points.Add(new DomainPoint(area, grade, entry.Text, entry.Achievement));
        }
        return points;
    }
}
