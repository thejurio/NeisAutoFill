namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 평가 하나를 <b>화면과 성적표에서 가리키는 이름</b>을 짓는다.
///
/// 한 영역에 평가가 여럿일 수 있다(사용자 확인 2026-08-22) — 잘함·보통·노력요함 한 세트가 평가 하나고,
/// 세트가 세 벌이면 평가가 셋이다. 그런데 성적표는 <b>열 이름으로</b> 평가를 구분하므로
/// 이름이 겹치면 안 된다. 그래서:
///
/// <list type="bullet">
///   <item>그 영역에 평가가 하나뿐이면 <b>영역명 그대로</b> (지금까지와 똑같이 보인다)</item>
///   <item>여럿이면 <c>영역 · 평가요소</c> 로 갈라 준다</item>
///   <item>그래도 겹치면 뒤에 <c>(2)</c>, <c>(3)</c> 을 붙인다</item>
/// </list>
///
/// <b>진짜 영역명은 따로 들고 다닌다</b>(<see cref="Models.CriteriaEntry"/>.Area) — 나이스
/// [평가계획(안)관리]에는 갈라 준 이름이 아니라 진짜 영역명이 들어가야 하기 때문이다.
/// </summary>
public static class PlanKeys
{
    public const string Separator = " · ";

    /// <summary>평가들의 (영역, 평가요소) 를 받아 <b>겹치지 않는</b> 이름을 순서대로 돌려준다.</summary>
    public static IReadOnlyList<string> Build(IReadOnlyList<(string Area, string Element)> items)
    {
        var perArea = items.GroupBy(i => i.Area.Trim())
            .ToDictionary(g => g.Key, g => g.Count());

        var used = new HashSet<string>();
        var keys = new List<string>(items.Count);

        foreach (var (rawArea, rawElement) in items)
        {
            var area = rawArea.Trim();
            var element = rawElement.Trim().Replace("\n", " ");

            var name = perArea[area] > 1 && element.Length > 0 ? area + Separator + element : area;

            // 그래도 겹치면 번호를 붙인다 — 평가요소가 비었거나 둘이 똑같은 경우
            var unique = name;
            for (var n = 2; !used.Add(unique); n++) unique = $"{name} ({n})";

            keys.Add(unique);
        }

        return keys;
    }
}
