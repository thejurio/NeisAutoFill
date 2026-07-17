namespace NeisAutoFill.Core.Matching;

/// <summary>
/// 입력 전 확인 창의 자동 제안 로직 (순수 — 전부 테스트됨).
///  - Suggest: 이름 유사도 제안 (정규화 후 일치 > 포함 > 편집거리 1)
///  - AssignAreasByOrder: 화면 영역 순서별 기본 선택 (이름 일치 자동 → 남는 영역 순서 제안 → 부족하면 제외)
/// </summary>
public static class SimilaritySuggester
{
    /// <summary>후보 중 source 와 가장 비슷한 것. 충분히 비슷한 게 없으면 null.</summary>
    public static string? Suggest(string source, IReadOnlyList<string> candidates)
    {
        var s = Normalize(source);
        string? best = null;
        int bestScore = int.MaxValue;
        foreach (var c in candidates)
        {
            var t = Normalize(c);
            int score;
            if (s == t) score = 0;
            else if (t.Contains(s) || s.Contains(t)) score = 1;
            else score = Levenshtein(s, t) <= 1 ? 2 : int.MaxValue;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return bestScore <= 2 ? best : null;
    }

    /// <summary>
    /// 화면 영역 순서(rows)별로 어떤 엑셀 영역을 기본 선택할지.
    /// 반환: 위치별 (선택 영역 또는 null=제외, 이름 일치로 자동 선택됐는지).
    /// </summary>
    public static IReadOnlyList<(string? Area, bool AutoPicked)> AssignAreasByOrder(
        IReadOnlyList<string?> screenAreas, int rows, IReadOnlyList<string> excelAreas)
    {
        var result = new (string? Area, bool AutoPicked)[rows];
        var used = new HashSet<string>();

        for (int i = 0; i < rows; i++)   // 1차: 화면 영역명과 같은 이름이 있으면 자동 선택
        {
            var screen = i < screenAreas.Count ? screenAreas[i] : null;
            if (screen is not null && excelAreas.Contains(screen) && used.Add(screen))
                result[i] = (screen, true);
        }

        var remaining = excelAreas.Where(a => !used.Contains(a)).ToList();
        int r = 0;
        for (int i = 0; i < rows; i++)   // 2차: 남은 위치엔 남은 영역을 순서대로 (모자라면 제외)
            if (result[i].Area is null)
                result[i] = (r < remaining.Count ? remaining[r++] : null, false);

        return result;
    }

    // 이름·영역 공통 경량 정규화 (공백·중점 제거) — NameNormalizer 는 괄호 접미 제거 등
    // 학생 이름 전용 규칙이라, 영역명에도 쓰는 유사도 비교에는 이 축약형을 쓴다.
    private static string Normalize(string s) =>
        new(s.Where(ch => !char.IsWhiteSpace(ch) && ch != '·' && ch != '.').ToArray());

    private static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 99;
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
