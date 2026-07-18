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

    /// <param name="nameMap">사용자 매핑(확인 창): 화면 성명 → 내 자료 성명 ("" = 제외). 이름이 달라 자동 매칭 안 될 때.</param>
    public static MatchResult Build(
        IReadOnlyDictionary<int, (string? No, string? Name)> rowMap,
        IReadOnlyList<NarrativeEntry> entries,
        IReadOnlyDictionary<string, string>? nameMap = null)
    {
        var byKey = new Dictionary<(string, string), NarrativeEntry>();
        var byName = new Dictionary<string, NarrativeEntry>();
        var counts = new Dictionary<string, int>();
        foreach (var e in entries)
        {
            var norm = NameNormalizer.Normalize(e.Name);
            byKey[(e.No, norm)] = e;
            byName[norm] = e;
            counts[norm] = counts.GetValueOrDefault(norm) + 1;
        }
        var dupNames = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet();   // 동명이인

        var todo = new List<Item>();
        var skipped = new List<SkipItem>();
        var used = new HashSet<NarrativeEntry>();

        foreach (var idx in rowMap.Keys.OrderBy(k => k))
        {
            var (no, name) = rowMap[idx];
            if (name is null) continue;   // 성명 없는 행은 대상 아님 (헤더·오버레이)

            NarrativeEntry? entry;
            // 사용자 매핑 우선 — 화면 성명이 매핑에 있으면 그 결정을 따른다
            if (nameMap is not null && nameMap.TryGetValue(name, out var mapped))
            {
                if (string.IsNullOrEmpty(mapped)) continue;   // 명시적 '입력 안 함'
                entry = byName.TryGetValue(NameNormalizer.Normalize(mapped), out var em) ? em : null;
            }
            else
            {
                var norm = NameNormalizer.Normalize(name);
                if (no is not null && byKey.TryGetValue((no, norm), out var e1)) entry = e1;
                // 동명이인은 이름만으로 특정하면 오입력 위험 → 폴백 금지, 사용자 매핑 유도
                else if (dupNames.Contains(norm))
                {
                    if (byName.TryGetValue(norm, out var amb)) used.Add(amb);   // 스킵 보고가 이 서술문을 잡도록
                    skipped.Add(new SkipItem(no ?? "", name, "",
                        "동명이인 — 화면 번호로 특정 불가 (확인 창에서 지정하세요)"));
                    continue;
                }
                else entry = byName.TryGetValue(norm, out var e2) ? e2 : null;
            }
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
