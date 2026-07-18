using NeisAutoFill.Core.Matching;

namespace NeisAutoFill.Core;

/// <summary>
/// 전과목 업로드 전, 내 자료의 과목명을 화면(나이스 콤보)의 실제 과목명에 대응시키는 순수 로직.
/// 정확 일치 → 정규화 후 일치 → 유사 추천 순으로 자동 제안하고, 나머지는 사용자가 고른다.
/// 교과평가·교과발달상황 양쪽의 전과목 업로드가 공용으로 쓴다.
/// </summary>
public static class SubjectMapper
{
    /// <summary>과목 한 건의 자동 매핑 제안. Screen=제안된 화면 과목( null = 자동 제안 없음 ), Auto=정확/정규화 일치로 자동 확정 가능.</summary>
    public sealed record Suggestion(string MySubject, string? Screen, bool Auto);

    /// <summary>내 과목들 → 화면 과목 목록에 대한 자동 매핑 제안.</summary>
    public static IReadOnlyList<Suggestion> Suggest(
        IReadOnlyList<string> mySubjects, IReadOnlyList<string> screenSubjects)
    {
        var result = new List<Suggestion>(mySubjects.Count);
        foreach (var mine in mySubjects)
        {
            // 1) 정확 일치
            var exact = screenSubjects.FirstOrDefault(s => s == mine);
            if (exact is not null) { result.Add(new Suggestion(mine, exact, true)); continue; }

            // 2) 정규화(괄호·공백 제거 등) 후 일치 — "국어" vs "국어 " vs "국어(1)"의 앞부분
            var normMine = Norm(mine);
            var normMatch = screenSubjects.FirstOrDefault(s => Norm(s) == normMine);
            if (normMatch is not null) { result.Add(new Suggestion(mine, normMatch, true)); continue; }

            // 3) 유사 추천 (기존 이름 유사도 재사용) — 자동 확정은 안 함(사용자 확인 필요)
            var similar = SimilaritySuggester.Suggest(mine, screenSubjects);
            result.Add(new Suggestion(mine, similar, false));
        }
        return result;
    }

    /// <summary>비교용 정규화: 괄호 안·공백·가운뎃점 제거. (예: "즐거운 생활" ~ "즐거운생활", "국어(1)" ~ "국어")</summary>
    private static string Norm(string s)
    {
        var cut = s;
        int paren = cut.IndexOfAny(new[] { '(', '（', '[' });
        if (paren > 0) cut = cut[..paren];
        return new string(cut.Where(c => !char.IsWhiteSpace(c) && c != '·' && c != '/').ToArray());
    }
}
