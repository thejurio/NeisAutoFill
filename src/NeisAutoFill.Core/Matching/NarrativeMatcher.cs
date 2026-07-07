using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Core.Matching;

/// <summary>
/// 서술문 화면의 행 지도(rowindex → 번호/성명)와 생성된 서술문을 매칭.
/// StudentMatcher(§4.4)와 같은 키 우선순위: (번호, 정규화이름) → 정규화이름.
/// </summary>
public static class NarrativeMatcher
{
    public sealed record Item(int RowIndex, NarrativeEntry Entry);

    public sealed record MatchResult(
        IReadOnlyList<Item> Todo,
        IReadOnlyList<SkipItem> Skipped);

    public static MatchResult Build(
        IReadOnlyDictionary<int, (string? No, string? Name)> rowMap,
        IReadOnlyList<NarrativeEntry> entries)
    {
        var byKey = new Dictionary<(string, string), NarrativeEntry>();
        var byName = new Dictionary<string, NarrativeEntry>();
        foreach (var e in entries)
        {
            var norm = NameNormalizer.Normalize(e.Name);
            byKey[(e.No, norm)] = e;
            byName[norm] = e;
        }

        var todo = new List<Item>();
        var skipped = new List<SkipItem>();
        var used = new HashSet<NarrativeEntry>();

        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name) = rowMap[idx];
            if (name is null) continue;   // 성명 없는 행은 대상 아님 (헤더·오버레이)

            var norm = NameNormalizer.Normalize(name);
            var entry = (no is not null && byKey.TryGetValue((no, norm), out var e1)) ? e1
                      : byName.TryGetValue(norm, out var e2) ? e2
                      : null;
            if (entry is null) continue;   // 서술문 없는 학생 행은 그냥 지나감 (스킵 사유 아님)

            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                skipped.Add(new SkipItem(entry.No, entry.Name, "", "서술문이 비어 있음"));
                used.Add(entry);
                continue;
            }
            todo.Add(new Item(idx, entry));
            used.Add(entry);
        }

        // 화면에서 행을 못 찾은 서술문 → 스킵 보고
        foreach (var e in entries.Where(e => !used.Contains(e)))
            skipped.Add(new SkipItem(e.No, e.Name, "", "화면에서 학생 행을 찾지 못함"));

        return new MatchResult(todo, skipped);
    }
}
