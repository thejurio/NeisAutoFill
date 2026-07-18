namespace NeisAutoFill.Core;

/// <summary>
/// 전담 담당 등록 (F9 M2, 순수 로직). "어느 학년·반의 어느 과목"을 담당하는지 담고,
/// 그로부터 작업 조합(TeachingUnit) 목록을 만든다.
///
/// 등록 방식: 학년별로 (담당 반들 + 담당 과목들)을 넣으면 교차로 조합이 생긴다.
/// 학년마다 과목이 다를 수 있게 학년 단위로 분리 저장 (예: 3학년은 영어만, 4학년은 과학만).
/// </summary>
public sealed class SubjectAssignment
{
    /// <summary>한 학년의 담당 — 반 목록 × 과목 목록.</summary>
    public sealed class GradeEntry
    {
        public int Grade { get; set; }
        public List<string> Classes { get; set; } = new();    // 예: ["1","2"]
        public List<string> Subjects { get; set; } = new();   // 예: ["영어"]
    }

    public List<GradeEntry> Grades { get; set; } = new();

    /// <summary>조합에서 제외할 항목 (행렬 교차 중 실제로는 안 하는 것). Display 문자열로.</summary>
    public List<string> Excluded { get; set; } = new();

    /// <summary>등록으로부터 실제 작업 조합 목록을 생성. 학년·반·과목 교차에서 제외분을 뺀다. 순서·중복 정리.</summary>
    public IReadOnlyList<TeachingUnit> BuildUnits()
    {
        var excluded = new HashSet<string>(Excluded);
        var seen = new HashSet<string>();
        var units = new List<TeachingUnit>();
        foreach (var g in Grades)
        {
            if (!SubjectModePaths.IsValidGrade(g.Grade)) continue;
            foreach (var cls in g.Classes)
            {
                if (!SubjectModePaths.IsValidClass(cls)) continue;
                foreach (var subj in g.Subjects)
                {
                    if (!SubjectModePaths.IsValidSubject(subj)) continue;
                    var u = new TeachingUnit(g.Grade, cls.Trim(), subj.Trim());
                    if (excluded.Contains(u.Display)) continue;
                    if (seen.Add(u.Display)) units.Add(u);
                }
            }
        }
        return units;
    }

    /// <summary>표시명으로 조합을 찾는다 (현재 선택 복원용). 없으면 null.</summary>
    public TeachingUnit? FindByDisplay(string? display) =>
        display is null ? null : BuildUnits().FirstOrDefault(u => u.Display == display);
}
