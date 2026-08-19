namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 한 과목의 기준 시수 (연간 시간표 문서 끝의 시수표).
/// </summary>
/// <param name="Subject">과목명 (국어·사회·… — 문서에 적힌 그대로)</param>
/// <param name="Ministry">교과부 기준</param>
/// <param name="Delta">증감 시수</param>
/// <param name="School">본교 기준 (= 교과부 기준 + 증감)</param>
/// <param name="FirstSemester">1학기 배정</param>
/// <param name="SecondSemester">2학기 배정</param>
public sealed record SubjectHourStandard(
    string Subject,
    int Ministry,
    int Delta,
    int School,
    int FirstSemester,
    int SecondSemester)
{
    /// <summary>그 학기의 기준 시수. 학기를 모르면 본교 기준 전체.</summary>
    public int For(int semester) => semester switch
    {
        1 => FirstSemester,
        2 => SecondSemester,
        _ => School,
    };
}

/// <summary>
/// 연간 시간표 문서 끝의 <b>시수표</b>를 읽는다 (실측 2026-08-19).
///
/// 표 생김새:
/// <code>
///              국어 사회 도덕 수학 과학 실과 체육 음악 미술 영어 창체   계
/// 교과부기준    204  102   34  136  102   68  102   68   68  102  102 1088
/// 증감시수        0    0    0    0    0    0    0    0    0    0    0    0
/// 본교기준      204  102   34  136  102   68  102   68   68  102  102 1088
/// 1학기         108   54   18   72   54   36   54   36   36   48   52  568
/// 본교 2학기     96   48   16   64   48   32   48   32   32   54   50  520
/// </code>
///
/// 글자를 이어 붙이면 <c>204102341361026810268681021021088</c> 처럼 숫자가 뭉개진다.
/// <b>과목 헤더의 X 좌표로 열을 잡고</b>, 각 줄의 숫자 덩어리를 가장 가까운 열에 배정한다.
/// </summary>
public static class TimetableHoursParser
{
    /// <summary>'계' 열은 합계라 과목이 아니다.</summary>
    private const string TotalLabel = "계";

    /// <summary>문서 전체에서 시수표를 찾는다. 없으면 빈 목록.</summary>
    public static IReadOnlyList<SubjectHourStandard> Parse(DocumentLayout layout)
    {
        foreach (var page in layout.Pages.Reverse())   // 보통 마지막 쪽에 있다
        {
            var found = ParsePage(page);
            if (found.Count > 0) return found;
        }
        return Array.Empty<SubjectHourStandard>();
    }

    private static IReadOnlyList<SubjectHourStandard> ParsePage(DocumentPage page)
    {
        var lines = page.Lines();

        var headerIndex = FindHeaderLine(lines);
        if (headerIndex < 0) return Array.Empty<SubjectHourStandard>();

        var columns = SplitGroups(lines[headerIndex])
            .Select(g => (Name: string.Concat(g.Select(x => x.Text)).Trim(), Center: Center(g)))
            .Where(c => c.Name.Length > 0 && c.Name != TotalLabel)
            .ToList();

        if (columns.Count < 3) return Array.Empty<SubjectHourStandard>();

        var leftEdge = lines[headerIndex].Min(g => g.X) - 6;   // 이보다 왼쪽은 행 이름이다

        var rows = new Dictionary<string, int[]>();
        foreach (var line in lines.Skip(headerIndex + 1))
        {
            var label = string.Concat(line.Where(g => g.X < leftEdge).Select(g => g.Text)).Trim();
            if (label.Length == 0) continue;

            // "(통합)" 줄은 괄호 안 값이라 건너뛴다
            if (label.Contains('(') || line.Any(g => g.Text is "(" or ")")) continue;

            var key = RowKey(label);
            if (key is null || rows.ContainsKey(key)) continue;

            var values = ReadNumbers(line.Where(g => g.X >= leftEdge).ToList(), columns);
            if (values is not null) rows[key] = values;
        }

        if (rows.Count == 0) return Array.Empty<SubjectHourStandard>();

        int Value(string key, int i) => rows.TryGetValue(key, out var v) ? v[i] : 0;

        return columns
            .Select((c, i) => new SubjectHourStandard(
                c.Name, Value("ministry", i), Value("delta", i),
                Value("school", i), Value("first", i), Value("second", i)))
            .ToList();
    }

    /// <summary>과목 이름이 늘어선 줄. 국어와 창체가 같이 있으면 그 줄이다.</summary>
    private static int FindHeaderLine(IReadOnlyList<IReadOnlyList<TextGlyph>> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var text = string.Concat(lines[i].Where(g => !g.IsInvisible).Select(g => g.Text));
            if (text.Contains("국어") && text.Contains("창체")) return i;
        }
        return -1;
    }

    private static string? RowKey(string label) =>
        label.Contains("교과부") ? "ministry" :
        label.Contains("증감") ? "delta" :
        label.Contains("본교기준") ? "school" :
        label.Contains("1학기") ? "first" :
        label.Contains("2학기") ? "second" :
        null;

    /// <summary>숫자 덩어리를 열에 배정한다. 열 수와 맞지 않으면 null — 잘못 읽느니 버린다.</summary>
    private static int[]? ReadNumbers(
        IReadOnlyList<TextGlyph> glyphs, IReadOnlyList<(string Name, double Center)> columns)
    {
        var values = new int?[columns.Count];

        foreach (var group in SplitGroups(glyphs))
        {
            var text = string.Concat(group.Select(g => g.Text)).Trim();
            if (text.Length == 0 || !text.All(char.IsAsciiDigit)) continue;
            if (!int.TryParse(text, out var number)) continue;

            var center = Center(group);
            var best = -1;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < columns.Count; i++)
            {
                var d = Math.Abs(columns[i].Center - center);
                if (d < bestDistance) { bestDistance = d; best = i; }
            }

            if (best >= 0 && values[best] is null) values[best] = number;
        }

        // 한 칸이라도 비면 표를 잘못 읽은 것이다
        return values.Any(v => v is null) ? null : values.Select(v => v!.Value).ToArray();
    }

    /// <summary>
    /// 한 줄의 글자를 <b>가로 간격</b>으로 끊어 덩어리로 만든다.
    /// 문자 폭보다 눈에 띄게 벌어지면 다른 칸이다 — 붙어 있는 숫자는 한 수로 읽는다.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<TextGlyph>> SplitGroups(IReadOnlyList<TextGlyph> line)
    {
        var sorted = line.Where(g => !g.IsInvisible).OrderBy(g => g.X).ToList();
        var groups = new List<IReadOnlyList<TextGlyph>>();
        if (sorted.Count == 0) return groups;

        var width = Median(sorted.Select(g => g.Width).Where(w => w > 0).ToList());
        var limit = (width > 0 ? width : 6) * 0.6;   // 글자 폭의 절반 넘게 벌어지면 다른 칸

        var current = new List<TextGlyph> { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i].X - (sorted[i - 1].X + sorted[i - 1].Width);
            if (gap > limit) { groups.Add(current); current = new List<TextGlyph>(); }
            current.Add(sorted[i]);
        }
        groups.Add(current);

        return groups;
    }

    private static double Center(IReadOnlyList<TextGlyph> group) =>
        (group[0].X + group[^1].X + group[^1].Width) / 2;

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }
}
