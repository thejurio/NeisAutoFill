using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Core;

/// <summary>인식 검수 경고 한 건. Domain 이 null 이면 과목 단위 경고.</summary>
public sealed record PlanWarning(string Subject, string? Domain, string Message, PlanWarningLevel Level);

public enum PlanWarningLevel { Info, Warn }

/// <summary>
/// AI/엑셀로 인식한 평가계획을 검수해, 오인식·누락이 의심되는 곳을 경고로 뽑는다.
/// "조용히 틀리는" 것을 막기 위한 순수 로직 — 실제 판정·수정은 사용자가 표에서.
/// </summary>
public static class PlanAudit
{
    /// <summary>이보다 짧은 기준 문구는 잘렸을 가능성으로 본다(등급 라벨 자체가 들어간 경우 등).</summary>
    private const int ShortCriteriaLen = 5;

    public static IReadOnlyList<PlanWarning> Analyze(
        IReadOnlyList<SubjectPlan> plans, IReadOnlyList<string> scaleLabels)
    {
        var labels = scaleLabels.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        var warnings = new List<PlanWarning>();

        foreach (var p in plans)
        {
            if (p.Domains.Count == 0)
            {
                warnings.Add(new PlanWarning(p.SubjectName, null, "인식된 평가영역이 없습니다.", PlanWarningLevel.Warn));
                continue;
            }

            foreach (var domain in p.Domains)
            {
                var present = new List<string>();
                foreach (var label in labels)
                {
                    if (p.Criteria.TryGetValue((domain, label), out var e) && !string.IsNullOrWhiteSpace(e.Text))
                        present.Add(label);
                }
                var missing = labels.Where(l => !present.Contains(l)).ToList();

                if (present.Count == 0)
                {
                    warnings.Add(new PlanWarning(p.SubjectName, domain,
                        "등급별 기준이 하나도 인식되지 않았습니다.", PlanWarningLevel.Warn));
                    continue;   // 전부 없으면 아래 짧은문구 검사는 의미 없음
                }
                if (missing.Count > 0)
                {
                    warnings.Add(new PlanWarning(p.SubjectName, domain,
                        $"{string.Join("·", missing)} 등급 기준이 없습니다.", PlanWarningLevel.Warn));
                }

                foreach (var label in present)
                {
                    var text = p.Criteria[(domain, label)].Text.Trim();
                    if (text.Length < ShortCriteriaLen)
                        warnings.Add(new PlanWarning(p.SubjectName, domain,
                            $"'{label}' 기준 문구가 너무 짧아 잘렸을 수 있습니다: \"{text}\"", PlanWarningLevel.Info));
                }
            }
        }
        return warnings;
    }

    /// <summary>과목별 경고 건수 요약 (UI 상태 표기용). Warn 만 센다.</summary>
    public static int WarnCount(IReadOnlyList<PlanWarning> warnings) =>
        warnings.Count(w => w.Level == PlanWarningLevel.Warn);
}
