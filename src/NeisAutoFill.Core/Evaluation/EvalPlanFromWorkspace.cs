using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// [자료 준비]에 들어 있는 평가계획을 나이스에 넣을 모양으로 바꾼다.
///
/// <b>왜 문서가 아니라 이것을 쓰나</b>: 사용자가 프로그램 안에서 계획을 고칠 수 있고,
/// 고친 그대로 나이스에 들어가야 하기 때문이다(사용자 요청 2026-08-22).
/// 문서를 다시 읽으면 그 수정이 통째로 무시된다.
///
/// <b>평가요소는 없다.</b> [자료 준비] 표에 그 칸이 없어서(영역 · 성취기준 · 단계별 내용뿐)
/// 나이스의 평가요소는 <b>빈 칸으로 들어간다</b>. 채우려면 자료 준비에 칸을 먼저 만들어야 한다.
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
            var areas = new List<EvalArea>();

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

                // 성취기준은 그 영역의 단계들이 함께 쓴다 — 처음 발견되는 값을 쓴다
                var standard = levels
                    .Select(level => plan.Criteria.TryGetValue((domain, level), out var e) ? e.Achievement : null)
                    .FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) ?? "";

                areas.Add(new EvalArea(domain, new[]
                {
                    new EvalStandard(standard.Trim(), Element: "", criteria),
                }));
            }

            if (areas.Count > 0) subjects.Add(new EvalSubjectPlan(plan.SubjectName, areas));
        }

        return new EvalPlanDocument(subjects);
    }
}
