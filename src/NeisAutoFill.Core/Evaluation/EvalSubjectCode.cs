using System.Text.RegularExpressions;

namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 성취기준 코드에서 교과를 읽는다 — <c>[6국04-05]</c> → 국어.
///
/// 문서에 교과 이름이 따로 적혀 있지 않은 쪽이 많아서, <b>코드가 가장 확실한 단서</b>다.
/// 세 양식(이지에듀 1·2학기, 스쿨마스터) 모두 이 꼴을 쓴다(실측 2026-08-21).
/// </summary>
public static partial class EvalSubjectCode
{
    [GeneratedRegex(@"\[\s*(\d)\s*([가-힣]{1,3})\s*\d")]
    private static partial Regex Pattern();

    /// <summary>코드 약자 → 교과명. 나이스 [교과(목)] 콤보의 이름과 맞춘다.</summary>
    private static readonly Dictionary<string, string> Names = new()
    {
        ["국"] = "국어", ["도"] = "도덕", ["사"] = "사회", ["수"] = "수학", ["과"] = "과학",
        ["실"] = "실과", ["체"] = "체육", ["음"] = "음악", ["미"] = "미술", ["영"] = "영어",
        ["바"] = "바른 생활", ["슬"] = "슬기로운 생활", ["즐"] = "즐거운 생활",
    };

    /// <summary>성취기준 글에서 교과명을 뽑는다. 못 알아보면 null — 사용자가 정해야 한다.</summary>
    public static string? Of(string standardText)
    {
        if (string.IsNullOrWhiteSpace(standardText)) return null;

        foreach (Match m in Pattern().Matches(standardText))
            if (Names.TryGetValue(m.Groups[2].Value, out var name)) return name;

        return null;
    }
}
