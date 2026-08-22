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
/// <b>두 모양이 완전히 겹치지 않는다.</b> 문서는 <c>영역 → 성취기준 여럿</c>인데
/// 표는 <c>영역 하나 = 성취기준 하나</c>다(<c>Criteria</c> 키가 (영역, 등급)이라 같은 영역을 두 번 못 담는다).
/// 그래서 성취기준이 여럿인 영역은 <b>한 칸에 합치고 알린다</b> — 버리지는 않는다.
/// 제대로 나누려면 평가 단위를 영역에서 성취기준으로 바꿔야 하고, 그건 성적표·나이스 입력까지 걸린다.
/// </summary>
public static class EvalPlanToWorkspace
{
    public static PlanConversion Convert(EvalPlanDocument document, GradeScale scale)
    {
        var plans = new List<SubjectPlan>();
        var notes = new List<PlanNote>();

        foreach (var subject in document.Subjects)
        {
            var domains = new List<string>();
            var criteria = new Dictionary<(string Domain, string Grade), CriteriaEntry>();

            foreach (var area in subject.Areas)
            {
                var standards = area.Standards.Where(s => s.Criteria.Count > 0).ToList();
                if (standards.Count == 0) continue;

                // 같은 영역이 문서 안에서 두 번 나오면 뒤엣것이 앞엣것을 지운다 — 합쳐서 한 줄로 둔다
                if (domains.Contains(area.Name))
                {
                    notes.Add(new(subject.Subject, $"'{area.Name}' 영역이 문서에 여러 번 나옵니다 — 한 줄로 합쳤습니다."));
                    continue;
                }

                if (standards.Count > 1)
                    notes.Add(new(subject.Subject,
                        $"'{area.Name}' 영역에 성취기준이 {standards.Count}개입니다 — 지금은 한 칸에 합쳐 넣었습니다."));

                var standardText = Join(standards.Select(s => s.Standard));
                var elementText = Join(standards.Select(s => s.Element));

                var byLevel = MapLevels(standards, scale, subject.Subject, area.Name, notes);
                if (byLevel.Count == 0) continue;

                domains.Add(area.Name);
                foreach (var (label, text) in byLevel)
                    criteria[(area.Name, label)] = new CriteriaEntry(text, standardText, elementText);
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
        IReadOnlyList<EvalStandard> standards, GradeScale scale,
        string subject, string area, List<PlanNote> notes)
    {
        var levels = scale.Levels.Select(l => l.Label).ToList();
        var result = new Dictionary<string, string>();

        // 성취기준이 여럿이면 같은 단계끼리 모아 합친다 (위에서 한 칸으로 합치기로 했으므로)
        var count = standards.Max(s => s.Criteria.Count);
        if (count != levels.Count)
            notes.Add(new(subject,
                $"'{area}' 의 평가단계가 {count}개인데 이 학교 척도는 {levels.Count}개입니다 — 겹치는 만큼만 옮겼습니다."));

        for (int i = 0; i < count && i < levels.Count; i++)
        {
            var texts = new List<string>();
            foreach (var standard in standards)
            {
                // 이름이 척도에 있으면 이름으로, 없으면 자리(i)로 찾는다
                var hit = standard.Criteria.FirstOrDefault(c => c.Level == levels[i])
                          ?? (i < standard.Criteria.Count ? standard.Criteria[i] : null);
                if (hit is not null && hit.Result.Trim().Length > 0) texts.Add(hit.Result.Trim());
            }
            if (texts.Count > 0) result[levels[i]] = Join(texts);
        }

        return result;
    }

    private static string Join(IEnumerable<string> parts) =>
        string.Join("\n", parts.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct());
}
