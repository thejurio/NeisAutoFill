using System.Globalization;
using System.Text;

namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 시간표 텍스트 비교용 정규화 (기술설계 §7).
/// 같은 뜻인데 표기만 다른 것(전각/반각, 가운뎃점 종류, 공백)을 하나로 모은다.
/// <b>표시용이 아니라 비교·키 생성용</b>이다 — 원문은 항상 따로 보관한다.
/// </summary>
public static class TimetableTextNormalizer
{
    // 가운뎃점 표기 흔들림: "자율·자치활동" / "자율ㆍ자치활동" / "자율・자치활동" / "자율.자치활동"
    // U+318D(ㆍ)는 NFKC 가 U+119E 로 바꾸므로 둘 다 넣는다 — 하나만 넣으면 정규화 순서에 따라 새어 나간다.
    private static readonly char[] MiddleDots =
        { '·', 'ㆍ', 'ᆞ', '・', '‧', '•', '．', '.' };

    /// <summary>비교용 정규화: 가운뎃점 통일 → 호환 정규화(NFKC) → 공백 제거 → 소문자.
    /// 가운뎃점을 NFKC 앞뒤로 모두 처리해 어느 표기가 들어와도 같은 결과가 나오게 한다.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var s = MapDots(text).Normalize(NormalizationForm.FormKC);   // 전각→반각 등 호환 문자 통일
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;                       // 공백 차이는 무시
            if (Array.IndexOf(MiddleDots, ch) >= 0) { sb.Append('·'); continue; }
            sb.Append(char.ToLower(ch, CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string MapDots(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s) sb.Append(Array.IndexOf(MiddleDots, ch) >= 0 ? '·' : ch);
        return sb.ToString();
    }

    /// <summary>괄호 짝이 맞는지. 안 맞으면 구조를 신뢰할 수 없으므로 해석을 포기한다.</summary>
    public static bool HasBalancedParens(string? text)
    {
        var depth = 0;
        foreach (var ch in text ?? string.Empty)
        {
            if (ch == '(') depth++;
            else if (ch == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }

    /// <summary>
    /// 표시용 다듬기: 양끝 공백과 <b>바깥을 감싼 괄호 한 겹</b>만 벗긴다 — "(창)" → "창".
    /// 안쪽 괄호는 뜻이 있을 수 있으므로 건드리지 않는다.
    /// </summary>
    public static string Trim(string? text)
    {
        var s = (text ?? string.Empty).Trim();
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')' && IsWrappingPair(s))
            s = s[1..^1].Trim();
        return s;
    }

    /// <summary>양끝 괄호가 서로 짝인지 — "(가)(나)" 처럼 별개 괄호 두 개를 잘못 벗기지 않기 위해.</summary>
    private static bool IsWrappingPair(string s)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')')
            {
                depth--;
                if (depth == 0) return i == s.Length - 1;   // 첫 '(' 의 짝이 마지막 문자여야 감싼 것
            }
        }
        return false;
    }
}
