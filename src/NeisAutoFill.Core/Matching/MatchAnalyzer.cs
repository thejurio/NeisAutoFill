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
        int RowsPerStudent,                                         // 화면 학생별 행 수 (대표값)
        IReadOnlyList<string> UnmatchedExcelStudents)              // 엑셀엔 있는데 화면에 없음 = 매핑 후보(남은 이름)
    {
        public bool Clean =>
            !SubjectMismatch && UnmatchedStudents.Count == 0 &&
            UnmatchedAreas.Count == 0 && !DuplicateAreas && !AreaCountMismatch;

        /// <summary>과목명만 다르고 학생·영역은 정상 — 이땐 복잡한 매핑 창 대신 "그래도 진행?" 만 물으면 된다.</summary>
        public bool SubjectOnlyMismatch =>
            SubjectMismatch && UnmatchedStudents.Count == 0 &&
            UnmatchedAreas.Count == 0 && !DuplicateAreas && !AreaCountMismatch;

        /// <summary>영역 쪽 문제가 하나도 없는가 (이름 이슈만 남은 상태 판정용, R8).</summary>
        public bool AreasClean =>
            UnmatchedAreas.Count == 0 && !DuplicateAreas && !AreaCountMismatch;

        /// <summary>이름 불일치 학생 전원이 캐시된 매핑(이전 결정, ""=입력 안 함 포함)에 들어 있는가 —
        /// true 면 창 없이 캐시를 재사용해도 안전하다 (배치 중 이름 매핑 1회 원칙, R8).</summary>
        public bool NamesCoveredBy(IReadOnlyDictionary<string, string>? map) =>
            map is not null && UnmatchedStudents.All(s => map.ContainsKey(s.Name));
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

        // 화면에 없는 엑셀 학생 = 자동 매칭 안 된 '남은 이름' → 매핑 후보로만 제공(자동 매칭된 이름은 뺀다)
        var screenNames = rowMap.Values.Where(v => v.Name is not null)
            .Select(v => NameNormalizer.Normalize(v.Name!)).ToHashSet();
        var unmatchedExcel = students.Where(s => !screenNames.Contains(NameNormalizer.Normalize(s.Name)))
            .Select(s => s.Name).ToList();

        return new Issues(
            screenSubject,
            screenSubject is not null && screenSubject != targetSubject,
            unmatchedStudents, screenAreas, unmatchedAreas,
            duplicateAreas, countMismatch, rowsPerStudent, unmatchedExcel);
    }

    /// <summary>
    /// 서술문(교과발달상황) 화면 분석 — 영역이 없으므로 과목·학생 불일치만 본다.
    /// entryNames = 내가 입력할 서술문 학생들의 성명. 화면에 있는데 내 자료에 없는 학생을 UnmatchedStudents 로.
    /// </summary>
    public static Issues AnalyzeNarratives(
        string? screenSubject,
        string targetSubject,
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<string> entryNames)
    {
        var mine = entryNames.Select(NameNormalizer.Normalize).ToHashSet();
        var unmatched = new List<(string, string)>();
        var seen = new HashSet<string>();

        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name, _) = rowMap[idx];
            if (name is null) continue;
            var key = $"{no}|{name}";
            if (!mine.Contains(NameNormalizer.Normalize(name)) && seen.Add(key))
                unmatched.Add((no ?? "", name));
        }

        // 화면에 없는 내 학생 = 남은 매핑 후보
        var screenNames = rowMap.Values.Where(v => v.Name is not null)
            .Select(v => NameNormalizer.Normalize(v.Name!)).ToHashSet();
        var unmatchedExcel = entryNames.Where(n => !screenNames.Contains(NameNormalizer.Normalize(n))).ToList();

        return new Issues(
            screenSubject,
            screenSubject is not null && screenSubject != targetSubject,
            unmatched,
            Array.Empty<string>(), Array.Empty<string>(),   // 영역 없음
            DuplicateAreas: false, AreaCountMismatch: false, RowsPerStudent: 0,
            UnmatchedExcelStudents: unmatchedExcel);
    }
}
