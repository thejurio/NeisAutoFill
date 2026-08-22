namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 한 영역에 평가가 여럿일 때 <b>속으로만 쓰는 구분 이름</b>을 짓고 되돌린다.
///
/// 평가 하나가 표의 한 줄이고 성적표의 한 열인데(사용자 확인 2026-08-22), 성적표는 열을
/// <b>이름으로</b> 가려내므로 같은 영역의 평가들이 서로 다른 이름을 가져야 한다.
/// 그래서 두 번째부터 <c>한국사#2</c>, <c>한국사#3</c> 처럼 꼬리를 붙인다.
///
/// <b>이 꼬리는 사람에게도 나이스에게도 보이지 않는다.</b> 화면 머리글과 나이스로 나가는 이름은
/// 언제나 <see cref="NameOf"/> 를 거친 <c>한국사</c> 다. 꼬리는 자료를 서로 구별하려고 붙일 뿐이다.
///
/// 평가가 하나뿐인 영역은 <b>꼬리가 없다</b> — 지금까지 만든 파일과 그대로 맞는다.
/// </summary>
public static class PlanKeys
{
    /// <summary>구분 꼬리를 여는 글자. 영역명에 잘 쓰이지 않는 것으로 골랐다.</summary>
    public const char Marker = '#';

    /// <summary>영역명들을 받아 <b>겹치지 않는</b> 이름을 순서대로 돌려준다.</summary>
    public static IReadOnlyList<string> Build(IReadOnlyList<string> areas)
    {
        var seen = new Dictionary<string, int>();
        var keys = new List<string>(areas.Count);

        foreach (var raw in areas)
        {
            var area = NameOf(raw.Trim());   // 이미 꼬리가 달린 값이 들어와도 두 번 붙지 않게
            var n = seen.TryGetValue(area, out var c) ? c + 1 : 1;
            seen[area] = n;

            keys.Add(n == 1 ? area : $"{area}{Marker}{n}");
        }

        return keys;
    }

    /// <summary>구분 꼬리를 뗀 <b>진짜 영역명</b>. 화면과 나이스에는 이것만 쓴다.</summary>
    public static string NameOf(string key)
    {
        var at = key.LastIndexOf(Marker);
        if (at <= 0 || at == key.Length - 1) return key;

        for (var i = at + 1; i < key.Length; i++)
            if (!char.IsDigit(key[i])) return key;

        return key[..at];
    }

    /// <summary>같은 영역이 두 번 이상 나오는가 — 그러면 이름만으로는 가릴 수 없다.</summary>
    public static bool HasRepeats(IEnumerable<string> keys) =>
        keys.Select(NameOf).GroupBy(n => n).Any(g => g.Count() > 1);
}
