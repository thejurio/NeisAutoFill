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

    /// <summary>
    /// 화면 행과 내 자료를 짝짓는 방식.
    ///
    /// <list type="bullet">
    ///   <item><b>ByName</b> — 학생 이름·번호와 영역명을 화면과 대조한다 (정확한 입력).</item>
    ///   <item><b>ByOrder</b> — 학생은 이름으로 찾고 <b>영역만</b> 순서로 맞춘다
    ///         (화면 영역명이 우리와 다르거나, 같은 영역에 평가가 여럿일 때).</item>
    ///   <item><b>ByRowOrder</b> — 대조 없이 <b>화면에 뜬 순서 그대로</b> 넣는다 (빠른 입력).
    ///         줄 수가 조금이라도 어긋나면 그 뒤가 통째로 밀리므로 <b>넣지 않고 멈춘다.</b></item>
    /// </list>
    /// </summary>
    public enum MatchMode { ByName, ByOrder, ByRowOrder }

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
        return mode switch
        {
            MatchMode.ByRowOrder => BuildByRowOrder(rowMap, students, scale, excelAreas),
            MatchMode.ByOrder => BuildByOrder(rowMap, students, scale, excelAreas, nameMap),
            _ => BuildByName(rowMap, students, scale, areaMap, nameMap),
        };
    }

    // ── 이름 기반 ─────────────────────────────
    private static MatchResult BuildByName(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        GradeScale scale,
        IReadOnlyDictionary<string, string>? areaMap,
        IReadOnlyDictionary<string, string>? nameMap)
    {
        var lk = BuildLookup(students);
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
            var student = Resolve(lk, no, name, nameMap);
            if (student is null)
            {
                skipped.Add(new SkipItem(no ?? "", name, area, SkipReasonFor(lk, no, name, nameMap)));
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

    // ── 줄 순서 기반 (빠른 입력) ────────────────

    /// <summary>
    /// <b>대조 없이 화면 순서대로</b> 넣는다. 화면 줄이 (학생 수 × 영역 수)와 정확히 같을 때만 한다 —
    /// 한 명이라도 빠지거나 더 있으면 그 뒤가 통째로 밀려 <b>엉뚱한 학생에게 성적이 들어간다.</b>
    /// 그래서 어긋나면 조용히 넘기지 않고 <b>아무것도 넣지 않고 멈춘다</b>(사용자 요청 2026-08-22).
    /// </summary>
    private static MatchResult BuildByRowOrder(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        GradeScale scale,
        IReadOnlyList<string> excelAreas)
    {
        var todo = new List<GradeTask>();
        var skipped = new List<SkipItem>();

        var rows = rowMap.Keys.OrderBy(k => k)
            .Select(i => (Idx: i, Meta: rowMap[i]))
            .Where(r => r.Meta.Name is not null)
            .ToList();

        var need = students.Count * excelAreas.Count;
        if (rows.Count != need)
            return new MatchResult(todo, skipped, MatchMode.ByRowOrder,
                $"빠른 입력을 멈췄습니다 — 화면 {rows.Count}줄이 명단 {students.Count}명 × 영역 " +
                $"{excelAreas.Count}개 = {need}줄과 다릅니다. 순서대로 넣으면 엉뚱한 학생에게 들어갑니다. " +
                "명단을 화면과 맞추거나 [정확한 입력]으로 바꿔 주세요.");

        for (var i = 0; i < students.Count; i++)
            for (var k = 0; k < excelAreas.Count; k++)
            {
                var (idx, meta) = rows[(i * excelAreas.Count) + k];
                var area = excelAreas[k];
                var screenArea = meta.Area ?? "";

                if (area == "")   // 사용자가 이 위치는 입력 제외 (부분 선택)
                {
                    skipped.Add(new SkipItem(meta.No ?? "", meta.Name!, screenArea, "사용자 제외 (순서)"));
                    continue;
                }

                var target = (students[i].Grades.TryGetValue(area, out var g) ? g : "")?.Trim() ?? "";
                AddTaskOrSkip(todo, skipped, idx, meta.No ?? "", meta.Name!, screenArea, target, scale);
            }

        return new MatchResult(todo.OrderBy(t => t.RowIndex).ToList(), skipped, MatchMode.ByRowOrder);
    }

    // ── 공통 헬퍼 ─────────────────────────────
    private sealed record RowRef(int Idx, string No, string Name, string Area);

    /// <summary>행 지도를 학생별로 묶는다 (엑셀 매칭 실패 행은 제외). 등장 순서 보존.</summary>
    private static List<(Student student, List<RowRef> rows)> GroupByStudent(
        IReadOnlyDictionary<int, RowMeta> rowMap, IReadOnlyList<Student> students,
        IReadOnlyDictionary<string, string>? nameMap = null)
    {
        var lk = BuildLookup(students);
        var map = new Dictionary<Student, List<RowRef>>();
        var order = new List<Student>();
        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name, area) = rowMap[idx];
            if (name is null || area is null) continue;
            var student = Resolve(lk, no, name, nameMap);
            if (student is null) continue;
            if (!map.TryGetValue(student, out var list)) { list = new(); map[student] = list; order.Add(student); }
            list.Add(new RowRef(idx, no ?? "", name, area));
        }
        return order.Select(s => (s, map[s])).ToList();
    }

    private sealed record Lookup(
        Dictionary<(string, string), Student> ByKey,
        Dictionary<string, Student> ByName,
        HashSet<string> DupNames);   // 엑셀에 같은 정규화 이름이 2명 이상 (동명이인)

    private static Lookup BuildLookup(IReadOnlyList<Student> students)
    {
        var byKey = new Dictionary<(string, string), Student>();
        var byName = new Dictionary<string, Student>();
        var counts = new Dictionary<string, int>();
        foreach (var s in students)
        {
            var norm = NameNormalizer.Normalize(s.Name);
            byKey[(s.No, norm)] = s;
            byName[norm] = s;
            counts[norm] = counts.GetValueOrDefault(norm) + 1;
        }
        var dup = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();
        return new Lookup(byKey, byName, dup);
    }

    /// <summary>화면 행이 동명이인이라 번호로 특정하지 못하는 경우인지 (이름 폴백이 위험).</summary>
    private static bool IsTwinAmbiguous(Lookup lk, string? no, string name, IReadOnlyDictionary<string, string>? nameMap)
    {
        if (nameMap is not null && nameMap.ContainsKey(name)) return false;   // 사용자가 지정했으면 문제 없음
        var norm = NameNormalizer.Normalize(name);
        if (!lk.DupNames.Contains(norm)) return false;                        // 동명이인 아님
        return no is null || !lk.ByKey.ContainsKey((no, norm));              // 번호로 특정 안 되면 애매
    }

    /// <summary>매칭 실패(Resolve=null) 시 스킵 사유 — 동명이인·사용자 제외·미존재 구분.</summary>
    private static string SkipReasonFor(Lookup lk, string? no, string name, IReadOnlyDictionary<string, string>? nameMap)
    {
        if (nameMap is not null && nameMap.TryGetValue(name, out var mn) && mn == "")
            return "사용자 제외 (학생)";
        if (IsTwinAmbiguous(lk, no, name, nameMap))
            return "동명이인 — 화면 번호로 특정 불가 (확인 창에서 지정하세요)";
        return "엑셀에 학생 없음";
    }

    private static Student? Resolve(
        Lookup lk, string? no, string name, IReadOnlyDictionary<string, string>? nameMap = null)
    {
        // 사용자 오버라이드: 화면 이름 → 엑셀 이름 ("" = 제외)
        if (nameMap is not null && nameMap.TryGetValue(name, out var mapped))
        {
            if (mapped == "") return null;
            return lk.ByName.TryGetValue(NameNormalizer.Normalize(mapped), out var sm) ? sm : null;
        }
        var norm = NameNormalizer.Normalize(name);
        if (no is not null && lk.ByKey.TryGetValue((no, norm), out var s1)) return s1;
        // 동명이인은 이름만으로 특정하면 오입력 위험 → 폴백 금지 (호출부가 스킵 사유 표시)
        if (lk.DupNames.Contains(norm)) return null;
        return lk.ByName.TryGetValue(norm, out var s2) ? s2 : null;
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
