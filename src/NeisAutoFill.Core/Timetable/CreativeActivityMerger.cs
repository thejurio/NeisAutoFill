namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 창체 전체 계획과 세부 계획을 하나로 묶는다 (기술설계 §8, D-009).
///
/// 같은 활동이 두 문서에 적혀 있으면 시수가 두 번 계산된다. 그래서
/// ① 날짜+교시+종류+활동명이 모두 같으면 자동 병합(대표는 더 구체적인 세부 계획)
/// ② 교시 정보만 다르면 자동으로 지우지 않고 <b>병합 후보로 제안</b>
/// ③ 같은 날 같은 활동인데 종류가 다르거나 교시가 맞부딪히면 <b>충돌</b>로 남긴다
///
/// 어느 경우에도 원본을 삭제하지 않는다 — 대표 일정의 출처 목록에 모두 보존한다.
/// </summary>
public static class CreativeActivityMerger
{
    public static CreativeMergeResult Merge(IEnumerable<CreativeActivityEvent> events)
    {
        var all = events.ToList();
        var merged = new List<MergedCreativeActivity>();
        var suggestions = new List<CreativeMergeSuggestion>();
        var conflicts = new List<CreativeMergeConflict>();

        // ① 정확히 같은 일정끼리 먼저 묶는다
        var exactGroups = all.GroupBy(e => e.ExactKey).ToList();
        var representatives = new List<MergedCreativeActivity>();
        foreach (var g in exactGroups)
        {
            var group = g.ToList();
            representatives.Add(new MergedCreativeActivity(PickRepresentative(group), group));
        }

        // ② 날짜+종류+활동명이 같은데 정확 키가 갈린 것들 = 교시 정보 차이
        foreach (var loose in representatives.GroupBy(m => m.Representative.LooseKey))
        {
            var group = loose.ToList();
            if (group.Count == 1) { merged.Add(group[0]); continue; }

            var periods = group.Select(m => m.Representative.Period).Distinct().ToList();

            // 한쪽만 교시를 모르는 경우: 같은 활동일 가능성이 높지만 단정하지 않는다
            if (periods.Contains(null) && periods.Count == 2)
            {
                var withPeriod = group.First(m => m.Representative.Period is not null);
                var withoutPeriod = group.First(m => m.Representative.Period is null);

                merged.Add(withPeriod);   // 구체적인 쪽을 남기되
                suggestions.Add(new CreativeMergeSuggestion(
                    withPeriod.Sources.Concat(withoutPeriod.Sources).ToList(),
                    "같은 날짜·활동인데 한쪽에만 교시가 있습니다. 같은 활동인지 확인해 주세요."));
                continue;
            }

            // 교시가 서로 다른 값으로 여럿 = 정말 다른 수업인지 오기인지 알 수 없다
            conflicts.Add(new CreativeMergeConflict(
                group[0].Representative.Date,
                group.SelectMany(m => m.Sources).ToList(),
                "같은 날짜·활동명인데 교시가 서로 다릅니다."));
        }

        // ③ 같은 날짜·활동명인데 창체 종류가 다른 경우 (자율 vs 진로 등)
        foreach (var byName in all.GroupBy(e => (e.Date, Name: TimetableTextNormalizer.Normalize(e.ActivityName))))
        {
            var kinds = byName.Select(e => e.Kind).Distinct().ToList();
            if (kinds.Count <= 1) continue;

            conflicts.Add(new CreativeMergeConflict(
                byName.Key.Date,
                byName.ToList(),
                "같은 날짜·활동명인데 창체 종류가 다릅니다."));

            // 종류가 갈린 항목은 확정 목록에서 빼 둔다 — 사람이 정할 때까지 실행하면 안 된다
            merged.RemoveAll(m =>
                m.Representative.Date == byName.Key.Date &&
                TimetableTextNormalizer.Normalize(m.Representative.ActivityName) == byName.Key.Name);
        }

        return new CreativeMergeResult(merged, suggestions, conflicts);
    }

    /// <summary>세부 계획이 있으면 그쪽을 대표로 — 교시·활동명이 더 구체적이다.</summary>
    private static CreativeActivityEvent PickRepresentative(List<CreativeActivityEvent> group) =>
        group.FirstOrDefault(e => e.Source == CreativeSourceKind.Detail) ?? group[0];
}
