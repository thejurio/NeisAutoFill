using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Core.Matching;

/// <summary>
/// 화면 행 지도(rowindex → RowMeta)와 엑셀 학생 데이터를 매칭해 입력할 GradeTask 목록을 산출.
///
/// 매칭 모드 (하이브리드):
///  1) 이름 기반 (기본·안전): NEIS 영역명 == 엑셀 영역명으로 매칭. 순서 무관.
///  2) 순서 기반 (폴백): 영역명이 중복되거나 엑셀에 없는 영역명이 있으면, 학생별로
///     NEIS 행 순서 ↔ 엑셀 영역 순서를 위치로 정렬. (사용자 확인 후에만 사용)
///     - 개수가 다르면 정렬 불가 → FatalError 로 중단.
/// </summary>
public static class StudentMatcher
{
    public sealed record MatchResult(
        IReadOnlyList<GradeTask> Todo,
        IReadOnlyList<SkipItem> Skipped,
        MatchMode Mode = MatchMode.ByName,
        string? FatalError = null);

    public enum MatchMode { ByName, ByOrder }

    /// <summary>이름 기반이 안전하지 않은 이유 (null 이면 이름 기반 OK).</summary>
    public static string? DetectNameProblem(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        IReadOnlyList<string> excelAreas)
    {
        if (excelAreas.Distinct().Count() != excelAreas.Count)
            return "엑셀 영역명이 중복됩니다.";

        var excelSet = excelAreas.ToHashSet();
        foreach (var (_, rows) in GroupByStudent(rowMap, students))
        {
            var areas = rows.Select(r => r.Area).ToList();
            if (areas.Distinct().Count() != areas.Count)
                return "나이스 화면에 같은 영역명이 여러 번 나옵니다.";
            var missing = areas.FirstOrDefault(a => !excelSet.Contains(a));
            if (missing is not null)
                return $"나이스 영역명 '{missing}'이(가) 엑셀에 없습니다.";
        }
        return null;
    }

    /// <param name="areaMap">화면 영역명 → 엑셀 영역명 오버라이드 (값 "" = 그 영역 입력 제외). 이름 기반에서만.</param>
    /// <param name="nameMap">화면 학생이름 → 엑셀 학생이름 오버라이드 (값 "" = 그 학생 입력 제외).</param>
    public static MatchResult Build(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        GradeScale scale,
        IReadOnlyList<string> excelAreas,
        MatchMode mode,
        IReadOnlyDictionary<string, string>? areaMap = null,
        IReadOnlyDictionary<string, string>? nameMap = null)
    {
        return mode == MatchMode.ByOrder
            ? BuildByOrder(rowMap, students, scale, excelAreas, nameMap)
            : BuildByName(rowMap, students, scale, areaMap, nameMap);
    }

    // ── 이름 기반 ─────────────────────────────
    private static MatchResult BuildByName(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        GradeScale scale,
        IReadOnlyDictionary<string, string>? areaMap,
        IReadOnlyDictionary<string, string>? nameMap)
    {
        var (byKey, byName) = BuildLookup(students);
        var todo = new List<GradeTask>();
        var skipped = new List<SkipItem>();

        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name, area) = rowMap[idx];
            if (name is null || area is null)
            {
                skipped.Add(new SkipItem(no ?? "", name ?? "", area ?? "", "행 파싱 불완전"));
                continue;
            }
            // 영역 오버라이드: 화면 영역명 → 엑셀 영역명 ("" = 사용자 제외)
            var excelArea = areaMap is not null && areaMap.TryGetValue(area, out var ma) ? ma : area;
            if (excelArea == "")
            {
                skipped.Add(new SkipItem(no ?? "", name, area, "사용자 제외 (영역)"));
                continue;
            }
            var student = Resolve(byKey, byName, no, name, nameMap);
            if (student is null)
            {
                skipped.Add(new SkipItem(no ?? "", name, area,
                    nameMap is not null && nameMap.TryGetValue(name, out var mn) && mn == ""
                        ? "사용자 제외 (학생)" : "엑셀에 학생 없음"));
                continue;
            }
            var target = (student.Grades.TryGetValue(excelArea, out var g) ? g : "")?.Trim() ?? "";
            // 리포트의 영역명은 화면 기준 (사용자가 화면과 대조 가능)
            if (!AddTaskOrSkip(todo, skipped, idx, no, name, area, target, scale)) { }
        }
        return new MatchResult(todo, skipped, MatchMode.ByName);
    }

    // ── 순서 기반 ─────────────────────────────
    private static MatchResult BuildByOrder(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        GradeScale scale,
        IReadOnlyList<string> excelAreas,
        IReadOnlyDictionary<string, string>? nameMap = null)
    {
        var todo = new List<GradeTask>();
        var skipped = new List<SkipItem>();
        var groups = GroupByStudent(rowMap, students, nameMap);

        // 학생별 NEIS 행 수 == 엑셀 영역 수 확인 (다르면 정렬 불가 → 중단)
        foreach (var (student, rows) in groups)
        {
            if (rows.Count != excelAreas.Count)
            {
                var first = rows[0];
                return new MatchResult(todo, skipped, MatchMode.ByOrder,
                    $"{first.No}번 {first.Name}: 나이스 평가 영역 수({rows.Count})가 " +
                    $"엑셀 영역 수({excelAreas.Count})와 다릅니다. 순서 입력을 진행할 수 없습니다.");
            }
        }

        // 매칭 안 된 행(엑셀에 학생 없음)은 스킵으로
        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name, area) = rowMap[idx];
            if (name is not null && area is not null &&
                !groups.Any(g => g.rows.Any(r => r.Idx == idx)))
                skipped.Add(new SkipItem(no ?? "", name, area, "엑셀에 학생 없음"));
        }

        foreach (var (student, rows) in groups)
        {
            var ordered = rows.OrderBy(r => r.Idx).ToList();
            for (int k = 0; k < ordered.Count; k++)
            {
                var row = ordered[k];
                var excelArea = excelAreas[k];   // 위치로 정렬
                if (excelArea == "")             // 사용자가 이 위치는 입력 제외 (부분 선택)
                {
                    skipped.Add(new SkipItem(row.No, row.Name, row.Area, "사용자 제외 (순서)"));
                    continue;
                }
                var target = (student.Grades.TryGetValue(excelArea, out var g) ? g : "")?.Trim() ?? "";
                // 로그·리포트엔 NEIS 영역명을 쓴다 (화면과 일치)
                AddTaskOrSkip(todo, skipped, row.Idx, row.No, row.Name, row.Area, target, scale);
            }
        }
        return new MatchResult(todo.OrderBy(t => t.RowIndex).ToList(), skipped, MatchMode.ByOrder);
    }

    // ── 공통 헬퍼 ─────────────────────────────
    private sealed record RowRef(int Idx, string No, string Name, string Area);

    /// <summary>행 지도를 학생별로 묶는다 (엑셀 매칭 실패 행은 제외). 등장 순서 보존.</summary>
    private static List<(Student student, List<RowRef> rows)> GroupByStudent(
        IReadOnlyDictionary<int, RowMeta> rowMap, IReadOnlyList<Student> students,
        IReadOnlyDictionary<string, string>? nameMap = null)
    {
        var (byKey, byName) = BuildLookup(students);
        var map = new Dictionary<Student, List<RowRef>>();
        var order = new List<Student>();
        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name, area) = rowMap[idx];
            if (name is null || area is null) continue;
            var student = Resolve(byKey, byName, no, name, nameMap);
            if (student is null) continue;
            if (!map.TryGetValue(student, out var list)) { list = new(); map[student] = list; order.Add(student); }
            list.Add(new RowRef(idx, no ?? "", name, area));
        }
        return order.Select(s => (s, map[s])).ToList();
    }

    private static (Dictionary<(string, string), Student> byKey, Dictionary<string, Student> byName)
        BuildLookup(IReadOnlyList<Student> students)
    {
        var byKey = new Dictionary<(string, string), Student>();
        var byName = new Dictionary<string, Student>();
        foreach (var s in students)
        {
            var norm = NameNormalizer.Normalize(s.Name);
            byKey[(s.No, norm)] = s;
            byName[norm] = s;
        }
        return (byKey, byName);
    }

    private static Student? Resolve(
        Dictionary<(string, string), Student> byKey, Dictionary<string, Student> byName,
        string? no, string name, IReadOnlyDictionary<string, string>? nameMap = null)
    {
        // 사용자 오버라이드: 화면 이름 → 엑셀 이름 ("" = 제외)
        if (nameMap is not null && nameMap.TryGetValue(name, out var mapped))
        {
            if (mapped == "") return null;
            return byName.TryGetValue(NameNormalizer.Normalize(mapped), out var sm) ? sm : null;
        }
        var norm = NameNormalizer.Normalize(name);
        if (no is not null && byKey.TryGetValue((no, norm), out var s1)) return s1;
        return byName.TryGetValue(norm, out var s2) ? s2 : null;
    }

    private static bool AddTaskOrSkip(
        List<GradeTask> todo, List<SkipItem> skipped,
        int idx, string? no, string name, string neisArea, string target, GradeScale scale)
    {
        if (string.IsNullOrEmpty(target))
        {
            skipped.Add(new SkipItem(no ?? "", name, neisArea, "엑셀에 영역값 없음"));
            return false;
        }
        if (!scale.Contains(target))
        {
            skipped.Add(new SkipItem(no ?? "", name, neisArea, $"허용외 등급 '{target}'"));
            return false;
        }
        todo.Add(new GradeTask(idx, no ?? "", name, neisArea, target));
        return true;
    }
}
