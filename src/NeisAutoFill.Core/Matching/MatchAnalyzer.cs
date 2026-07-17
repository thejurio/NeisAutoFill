using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Core.Matching;

/// <summary>
/// 입력 실행 전 화면(행지도)과 엑셀 자료의 불일치 분석.
/// 문제가 하나도 없으면(Clean) 미리보기 창 없이 바로 실행하고,
/// 있으면 항목별로 사용자 결정(매핑·제외·순서)을 받는다.
/// </summary>
public static class MatchAnalyzer
{
    public sealed record Issues(
        string? ScreenSubject,
        bool SubjectMismatch,
        IReadOnlyList<(string No, string Name)> UnmatchedStudents,  // 화면에 있는데 엑셀에 없음
        IReadOnlyList<string> ScreenAreas,                          // 화면 영역 (등장 순서, 중복 제거)
        IReadOnlyList<string> UnmatchedAreas,                       // 엑셀에 없는 화면 영역명
        bool DuplicateAreas,                                        // 화면 한 학생에 같은 영역명 중복
        bool AreaCountMismatch,                                     // 학생별 화면 행 수 ≠ 엑셀 영역 수
        int RowsPerStudent)                                         // 화면 학생별 행 수 (대표값)
    {
        public bool Clean =>
            !SubjectMismatch && UnmatchedStudents.Count == 0 &&
            UnmatchedAreas.Count == 0 && !DuplicateAreas && !AreaCountMismatch;
    }

    public static Issues Analyze(
        string? screenSubject,
        string targetSubject,
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        IReadOnlyList<string> excelAreas)
    {
        var excelNames = students.Select(s => NameNormalizer.Normalize(s.Name)).ToHashSet();
        var excelAreaSet = excelAreas.ToHashSet();

        var unmatchedStudents = new List<(string, string)>();
        var seenStudents = new HashSet<string>();
        var screenAreas = new List<string>();
        var rowsByStudent = new Dictionary<string, List<string>>();   // 학생키 → 영역들 (행 순서)

        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name, area) = rowMap[idx];
            if (name is null || area is null) continue;

            if (!screenAreas.Contains(area)) screenAreas.Add(area);

            var key = $"{no}|{name}";
            if (!rowsByStudent.TryGetValue(key, out var list)) rowsByStudent[key] = list = new();
            list.Add(area);

            if (!excelNames.Contains(NameNormalizer.Normalize(name)) && seenStudents.Add(key))
                unmatchedStudents.Add((no ?? "", name));
        }

        var unmatchedAreas = screenAreas.Where(a => !excelAreaSet.Contains(a)).ToList();
        bool duplicateAreas = rowsByStudent.Values.Any(v => v.Distinct().Count() != v.Count);
        int rowsPerStudent = rowsByStudent.Values.Select(v => v.Count).DefaultIfEmpty(0).Max();
        bool countMismatch = rowsByStudent.Values.Any(v => v.Count != excelAreas.Count);

        return new Issues(
            screenSubject,
            screenSubject is not null && screenSubject != targetSubject,
            unmatchedStudents, screenAreas, unmatchedAreas,
            duplicateAreas, countMismatch, rowsPerStudent);
    }
}
