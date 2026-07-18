namespace NeisAutoFill.Core;

/// <summary>
/// 생성된 서술문 품질 점검(순수 로직). 학생 간 '복붙 의심'(매우 유사한 서술문) 그룹을 찾는다.
/// 문자 트라이그램 자카드 유사도 + union-find 로 상호 유사한 항목을 묶는다.
/// </summary>
public static class NarrativeQuality
{
    /// <summary>서로 매우 유사한 서술문들의 인덱스 그룹(각 그룹 2개 이상). threshold=유사도 하한(0~1).</summary>
    public static IReadOnlyList<IReadOnlyList<int>> SimilarGroups(
        IReadOnlyList<string> texts, double threshold = 0.85)
    {
        int n = texts.Count;
        var grams = new HashSet<string>[n];
        for (int i = 0; i < n; i++) grams[i] = Trigrams(texts[i]);

        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Jaccard(grams[i], grams[j]) >= threshold)
                    parent[Find(i)] = Find(j);

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            var root = Find(i);
            if (!groups.TryGetValue(root, out var list)) groups[root] = list = new List<int>();
            list.Add(i);
        }
        return groups.Values.Where(g => g.Count >= 2).Select(g => (IReadOnlyList<int>)g).ToList();
    }

    private static HashSet<string> Trigrams(string text)
    {
        var s = new string((text ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        var set = new HashSet<string>();
        for (int i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        int inter = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        int union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
