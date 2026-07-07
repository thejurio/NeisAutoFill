using System.Text.RegularExpressions;

namespace NeisAutoFill.Core.Matching;

/// <summary>이름 정규화. §4.4 — 괄호 접미어 제거 "박서연(전입학)" → "박서연".</summary>
public static partial class NameNormalizer
{
    [GeneratedRegex(@"\(.*?\)\s*$")]
    private static partial Regex ParenSuffix();

    public static string Normalize(string? name) =>
        ParenSuffix().Replace((name ?? string.Empty).Trim(), string.Empty).Trim();
}
