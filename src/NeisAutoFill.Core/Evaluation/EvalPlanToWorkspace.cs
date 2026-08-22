using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Core.Evaluation;

/// <summary>옮기는 과정에서 사용자가 알아야 할 것 하나.</summary>
/// <param name="Subject">어느 교과에서 생겼나</param>
/// <param name="Text">사람이 읽을 문장</param>
public sealed record PlanNote(string Subject, string Text);

/// <summary>문서에서 읽은 계획을 프로그램 표로 옮긴 결과.</summary>
public sealed record PlanConversion(IReadOnlyList<SubjectPlan> Plans, IReadOnlyList<PlanNote> Notes);

/// <summary>
/// 문서에서 읽은 <see cref="EvalPlanDocument"/> 를 [평가계획] 표가 쓰는 <see cref="SubjectPlan"/> 로 옮긴다.
/// (<see cref="EvalPlanFromWorkspace"/> 의 반대 방향이다.)
///
/// <b>평가 하나가 표의 한 줄</b>이다. 한 영역에 평가가 여럿이면 그만큼 줄이 늘어난다 —
/// 잘함·보통·노력요함 한 세트가 평가 하나이고, 그 경계는 평가요소다(사용자 확인 2026-08-22).
/// 줄 이름이 겹치지 않도록 <see cref="PlanKeys"/> 가 <c>영역 · 평가요소</c> 로 갈라 주고,
/// 진짜 영역명은 <c>CriteriaEntry.Area</c> 에 그대로 남는다.
/// </summary>
public static class EvalPlanToWorkspace
{
    public static PlanConversion Convert(EvalPlanDocument document, GradeScale scale)
    {
        var plans = new List<SubjectPlan>();
        var notes = new List<PlanNote>();

        foreach (var subject in document.Subjects)
        {
            // 평가 하나 = 표의 한 줄. 한 영역에 여럿이면 그만큼 줄이 늘어난다.
            var items = subject.Areas
                .SelectMany(a => a.Standards.Where(s => s.Criteria.Count > 0)
                                            .Select(s => (Area: a.Name, Standard: s)))
                .ToList();
            if (items.Count == 0) continue;

            var keys = PlanKeys.Build(items.Select(i => (i.Area, i.Standard.Element)).ToList());

            var domains = new List<string>();
            var criteria = new Dictionary<(string Domain, string Grade), CriteriaEntry>();

            for (var i = 0; i < items.Count; i++)
            {
                var (area, standard) = items[i];
                var byLevel = MapLevels(standard, scale, subject.Subject, area, notes);
                if (byLevel.Count == 0) continue;

                domains.Add(keys[i]);
                foreach (var (label, text) in byLevel)
                    criteria[(keys[i], label)] = new CriteriaEntry(
                        text, standard.Standard.Trim(), standard.Element.Trim(), area.Trim());
            }

            if (domains.Count > 0) plans.Add(new SubjectPlan(subject.Subject, domains, criteria));
        }

        return new PlanConversion(plans, notes);
    }

    /// <summary>
    /// 문서의 단계 이름을 이 학교 척도의 등급으로 맞춘다.
    ///
    /// 이름이 그대로 맞으면 그대로 쓰고, 아니면 <b>순서로</b> 맞춘다 — 문서의 단계도 척도도
    /// 위가 높은 쪽이다. 개수가 다르면 겹치는 만큼만 옮기고 알린다.
    /// </summary>
    private static Dictionary<string, string> MapLevels(
        EvalStandard standard, GradeScale scale,
        string subject, string area, List<PlanNote> notes)
    {
        var levels = scale.Levels.Select(l => l.Label).ToList();
        var result = new Dictionary<string, string>();

        if (standard.Criteria.Count != levels.Count)
            notes.Add(new(subject,
                $"'{area}' 의 평가단계가 {standard.Criteria.Count}개인데 이 학교 척도는 {levels.Count}개입니다 — 겹치는 만큼만 옮겼습니다."));

        for (var i = 0; i < levels.Count; i++)
        {
            // 이름이 척도에 있으면 이름으로, 없으면 자리(i)로 찾는다
            var hit = standard.Criteria.FirstOrDefault(c => c.Level == levels[i])
                      ?? (i < standard.Criteria.Count ? standard.Criteria[i] : null);

            if (hit is not null && hit.Result.Trim().Length > 0) result[levels[i]] = hit.Result.Trim();
        }

        return result;
    }
}
