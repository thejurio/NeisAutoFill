using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// [자료 준비]에 들어 있는 평가계획을 나이스에 넣을 모양으로 바꾼다.
///
/// <b>왜 문서가 아니라 이것을 쓰나</b>: 사용자가 프로그램 안에서 계획을 고칠 수 있고,
/// 고친 그대로 나이스에 들어가야 하기 때문이다(사용자 요청 2026-08-22).
/// 문서를 다시 읽으면 그 수정이 통째로 무시된다.
///
/// <b>평가요소도 함께 간다</b>(2026-08-22 부터) — 계획 표에 [평가요소] 칸이 생겼다.
/// 그 칸이 비어 있으면 나이스에도 빈 칸으로 들어간다.
/// </summary>
public static class EvalPlanFromWorkspace
{
    /// <summary>
    /// 교과별 계획을 옮긴다.
    /// </summary>
    /// <param name="plans">[자료 준비]가 들고 있는 교과별 계획</param>
    /// <param name="levels">평가단계 이름을 <b>높은 것부터</b> (잘함 · 보통 · 노력요함)</param>
    public static EvalPlanDocument Convert(
        IEnumerable<SubjectPlan> plans, IReadOnlyList<string> levels)
    {
        var subjects = new List<EvalSubjectPlan>();

        foreach (var plan in plans)
        {
            // 표의 한 줄 = 평가 하나. 나이스는 <b>영역 아래에 성취기준 여럿</b>을 받으므로
            // 같은 영역의 평가들을 도로 한 영역으로 모은다 (사용자 확인 2026-08-22).
            var areas = new List<EvalArea>();
            var byArea = new Dictionary<string, List<EvalStandard>>();
            var order = new List<string>();

            foreach (var domain in plan.Domains)
            {
                var criteria = levels
                    .Select(level => plan.Criteria.TryGetValue((domain, level), out var entry)
                        ? new EvalCriterion(level, entry.Text)
                        : null)
                    .OfType<EvalCriterion>()
                    .Where(c => c.Result.Trim().Length > 0)
                    .ToList();

                if (criteria.Count == 0) continue;

                // 성취기준·평가요소·영역은 그 줄의 단계들이 함께 쓴다 — 처음 발견되는 값을 쓴다
                string First(Func<Models.CriteriaEntry, string?> pick) => levels
                    .Select(level => plan.Criteria.TryGetValue((domain, level), out var e) ? pick(e) : null)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

                var area = First(e => e.Area) is { Length: > 0 } a ? a : domain;

                if (!byArea.TryGetValue(area, out var list))
                {
                    byArea[area] = list = new List<EvalStandard>();
                    order.Add(area);
                }

                list.Add(new EvalStandard(First(e => e.Achievement), First(e => e.Element), criteria));
            }

            foreach (var area in order) areas.Add(new EvalArea(area, byArea[area]));

            if (areas.Count > 0) subjects.Add(new EvalSubjectPlan(plan.SubjectName, areas));
        }

        return new EvalPlanDocument(subjects);
    }
}
