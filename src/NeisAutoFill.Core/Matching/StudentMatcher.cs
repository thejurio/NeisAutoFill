using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Core.Matching;

/// <summary>
/// 화면 행 지도(rowindex → RowMeta)와 엑셀 학생 데이터를 매칭해
/// 입력할 GradeTask 목록과 건너뜀 사유를 산출한다. §4.4 알고리즘.
/// 화이트리스트는 고정 집합이 아니라 활성 척도(GradeScale) 라벨 기준. (§9.2 동적화)
/// </summary>
public static class StudentMatcher
{
    public sealed record MatchResult(
        IReadOnlyList<GradeTask> Todo,
        IReadOnlyList<SkipItem> Skipped);

    public static MatchResult Build(
        IReadOnlyDictionary<int, RowMeta> rowMap,
        IReadOnlyList<Student> students,
        GradeScale scale)
    {
        // 키 우선순위: (번호, 정규화이름) → 정규화이름
        var byKey = new Dictionary<(string, string), Student>();
        var byName = new Dictionary<string, Student>();
        foreach (var s in students)
        {
            var norm = NameNormalizer.Normalize(s.Name);
            byKey[(s.No, norm)] = s;
            byName[norm] = s;   // 동명이인 시 마지막 우선 (번호 키가 먼저 걸리므로 실무상 영향 적음)
        }

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

            var norm = NameNormalizer.Normalize(name);
            var student = (no is not null && byKey.TryGetValue((no, norm), out var s1)) ? s1
                        : byName.TryGetValue(norm, out var s2) ? s2
                        : null;

            if (student is null)
            {
                skipped.Add(new SkipItem(no ?? "", name, area, "엑셀에 학생 없음"));
                continue;
            }

            var target = (student.Grades.TryGetValue(area, out var g) ? g : "")?.Trim() ?? "";
            if (string.IsNullOrEmpty(target))
            {
                skipped.Add(new SkipItem(no ?? "", name, area, "엑셀에 영역값 없음"));
                continue;
            }

            if (!scale.Contains(target))
            {
                skipped.Add(new SkipItem(no ?? "", name, area, $"허용외 등급 '{target}'"));
                continue;
            }

            todo.Add(new GradeTask(idx, no ?? "", name, area, target));
        }

        return new MatchResult(todo, skipped);
    }
}
