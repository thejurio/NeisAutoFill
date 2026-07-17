using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Core;

/// <summary>성취기준 참조 패널의 표시 항목 구성 (순수 — 테스트됨).</summary>
public static class CriteriaPanelBuilder
{
    public sealed record LevelView(string Grade, string Text);
    public sealed record DomainView(string Domain, string? Achievement, IReadOnlyList<LevelView> Levels);

    /// <summary>과목 계획 → 영역별 (성취기준, 등급별 기준) 목록. 척도 순서를 따른다.</summary>
    public static IReadOnlyList<DomainView> Build(SubjectPlan plan, GradeScale scale)
    {
        var labels = scale.Levels.Select(l => l.Label).ToList();
        return plan.Domains.Select(domain =>
        {
            var levels = labels
                .Select(g => plan.Criteria.TryGetValue((domain, g), out var e)
                    ? new LevelView(g, e.Text) : null)
                .Where(v => v is not null).Cast<LevelView>().ToList();
            var ach = labels
                .Select(g => plan.Criteria.TryGetValue((domain, g), out var e) ? e.Achievement : null)
                .FirstOrDefault(a => !string.IsNullOrEmpty(a));
            return new DomainView(domain, ach, levels);
        }).ToList();
    }
}
