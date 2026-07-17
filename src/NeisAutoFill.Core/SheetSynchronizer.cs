using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Core;

/// <summary>
/// 명단·평가계획 변경을 성적 시트에 반영하는 순수 로직 (UI 무관 — 전부 테스트됨).
/// 규칙:
///  - 명단 변경(추가/삭제/번호변경)은 모든 과목에 적용, 성적은 (번호,이름)→이름 순으로 이월
///  - 영역은 계획이 있으면 계획을 따르고, 개수가 같은 개명은 위치 기준으로 성적을 따라가게 함
///  - 빈 명단: rosterAuthoritative=false 면 정보 없음으로 보고 기존 학생 유지,
///    true 면(사용자가 명단을 실제로 비움 — 명단 시트가 존재) 전원 삭제로 반영
/// </summary>
public static class SheetSynchronizer
{
    /// <summary>명단 순서대로 학생을 배치한 과목 시트 생성. 기존 시트가 있으면 성적·특기사항 이월.</summary>
    public static SubjectSheet BuildSheet(
        string subjectName,
        IReadOnlyList<string> areas,
        SubjectSheet? old,
        IReadOnlyList<(string No, string Name)> roster,
        bool rosterAuthoritative = false)
    {
        var renameMap = BuildAreaRenameMap(old?.Areas, areas);

        if (roster.Count == 0 && !rosterAuthoritative)
            return new SubjectSheet(subjectName, areas,
                old?.Students.Select(s => Carry(s.No, s.Name, s, renameMap)).ToList() ?? new List<Student>());

        var byKey = old?.Students.ToDictionary(s => (s.No, s.Name)) ?? new();
        var byName = new Dictionary<string, Student>();
        if (old is not null)
            foreach (var s in old.Students) byName[s.Name] = s;

        var students = roster.Select(r =>
        {
            var prev = byKey.TryGetValue((r.No, r.Name), out var p1) ? p1
                     : byName.TryGetValue(r.Name, out var p2) ? p2 : null;   // 번호가 바뀐 학생도 이름으로 이월
            return Carry(r.No, r.Name, prev, renameMap);
        }).ToList();

        return new SubjectSheet(subjectName, areas, students);
    }

    /// <summary>
    /// 영역 개명 감지 (옛 이름 → 새 이름). 영역 개수가 같을 때, 같은 위치의 이름이 바뀌었고
    /// 옛 이름이 새 목록에 더 이상 없으면 개명으로 본다 — 성적이 새 이름을 따라가게 한다.
    /// (개수가 다르면 추가/삭제와 뒤섞여 모호하므로 이름 일치만 이월)
    /// </summary>
    public static Dictionary<string, string>? BuildAreaRenameMap(
        IReadOnlyList<string>? oldAreas, IReadOnlyList<string> newAreas)
    {
        if (oldAreas is null || oldAreas.Count != newAreas.Count) return null;
        Dictionary<string, string>? map = null;
        for (int i = 0; i < newAreas.Count; i++)
        {
            if (oldAreas[i] != newAreas[i] && !newAreas.Contains(oldAreas[i]))
                (map ??= new())[oldAreas[i]] = newAreas[i];
        }
        return map;
    }

    /// <summary>영역 구성과 학생(번호,이름) 목록이 같은지 — 같으면 표를 다시 만들 필요 없음.</summary>
    public static bool ShapeEquals(SubjectSheet a, SubjectSheet b) =>
        a.Areas.SequenceEqual(b.Areas) &&
        a.Students.Select(s => (s.No, s.Name)).SequenceEqual(b.Students.Select(s => (s.No, s.Name)));

    /// <summary>학생 성적 이월 — 개명된 영역은 새 이름 키로 옮긴다.</summary>
    private static Student Carry(string no, string name, Student? prev, Dictionary<string, string>? renameMap)
    {
        var grades = new Dictionary<string, string>();
        if (prev is not null)
            foreach (var (area, grade) in prev.Grades)
                grades[renameMap is not null && renameMap.TryGetValue(area, out var renamed) ? renamed : area] = grade;
        return new Student(no, name, grades, prev?.SpecialNote);
    }
}
