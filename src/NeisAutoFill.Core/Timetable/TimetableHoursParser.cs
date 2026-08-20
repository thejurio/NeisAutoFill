namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 한 과목의 기준 시수 (연간 시간표 문서의 시수표).
/// </summary>
/// <param name="Subject">과목명 (문서에 적힌 그대로)</param>
/// <param name="Ministry">교과부 기준. 문서에 없으면 0</param>
/// <param name="Delta">증감 시수</param>
/// <param name="School">본교 기준 (= 연간 계)</param>
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
    /// <summary>그 학기의 기준 시수. 학기를 모르면 연간 전체.</summary>
    public int For(int semester) => semester switch
    {
        1 => FirstSemester,
        2 => SecondSemester,
        _ => School,
    };
}

/// <summary>
/// 연간 시간표 문서의 <b>시수표</b>를 읽는다.
///
/// 이 표는 파서의 정답지다 — 읽어낸 칸 수가 여기 숫자와 맞아야 한다.
/// 실제로 이 검산으로 파싱 결함 셋을 잡았다(실기기검증 §4-K).
///
/// 양식마다 생김새가 다르다 (실측 2026-08-20):
/// <code>
/// 이지에듀    국어 사회 … 창체  계        스쿨마스터  과목 국어 … 진로 창체 A 총계
///  교과부기준  204  102 …  102 1088                    1학기계  98 … 32  5 78 578
///  증감시수      0    0 …    0    0                    2학기계  96 … 27  0 46 510
///  본교기준    204  102 …  102 1088                    연간계  194 … 59 24 102 1088
///  1학기       108   54 …   52  568
///  본교 2학기   96   48 …   50  520
/// </code>
///
/// <b>양식을 판별하지 않는다.</b> 행 이름과 열 위치만 보고 읽으므로,
/// 새 양식이 와도 이름만 알아보면 그대로 읽힌다.
/// 표가 <b>여러 쪽에 걸쳐</b> 있을 수 있어(스쿨마스터는 1학기와 2학기가 다른 쪽에 있다)
/// 쪽마다 읽어 과목 이름으로 합친다.
/// </summary>
public static class TimetableHoursParser
{
    /// <summary>합계 열은 과목이 아니다.</summary>
    private static readonly string[] TotalLabels = { "계", "총계", "합계" };

    /// <summary>맨 왼쪽 구분 열 이름 — 과목이 아니다.</summary>
    private static readonly string[] CornerLabels = { "과목", "교과", "구분", "영역" };

    /// <summary>
    /// 헤더에서 알아보는 이름들. <b>긴 이름부터</b> 맞춰야 "자율"이 "자율·자치활동"을 잘라먹지 않는다.
    /// 사전에 없는 이름(학교자율시간을 "A"로 적는 등)은 한 글자씩 제 열이 된다.
    /// </summary>
    private static readonly string[] Vocabulary =
    {
        "창의적체험활동", "창의적 체험활동", "슬기로운생활", "슬기로운 생활",
        "즐거운생활", "즐거운 생활", "바른생활", "바른 생활",
        "자율·자치활동", "자율자치활동", "동아리활동", "진로활동", "봉사활동", "학교자율시간",
        "국어", "사회", "도덕", "수학", "과학", "실과", "체육", "음악", "미술", "영어",
        "창체", "자율", "동아리", "진로", "봉사", "통합교과", "통합",
        // 두 줄로 쪼개 적는 양식이 있다 — 윗줄의 앞부분만 잡고 아래에서 정식 이름으로 편다
        // (1학년 이지에듀: 윗줄 "바른 슬기로운 즐거운 창의적" / 아랫줄 "생활 생활 체험활동")
        "슬기로운", "즐거운", "바른", "창의적",
        "총계", "합계", "과목", "교과", "구분", "영역", "계",
    };

    /// <summary>두 줄로 쪼개진 이름을 정식 이름으로 편다.</summary>
    private static string Expand(string name) => name switch
    {
        "바른" => "바른생활",
        "슬기로운" => "슬기로운생활",
        "즐거운" => "즐거운생활",
        "창의적" => "창체",
        _ => name,
    };

    /// <summary>진짜 과목 열인지 — 이것이 나오기 전 열은 왼쪽 구분 칸이라 버린다.</summary>
    private static bool IsSubjectColumn(string name) =>
        SubjectHints.Any(h => name.StartsWith(h)) ||
        name is "바른생활" or "슬기로운생활" or "즐거운생활" or "창체";

    /// <summary>헤더인지 알아보는 데 쓰는 과목 이름들.</summary>
    private static readonly string[] SubjectHints =
    {
        "국어", "사회", "도덕", "수학", "과학", "실과", "체육", "음악", "미술", "영어",
        "창체", "자율", "동아리", "진로", "바른", "슬기로운", "즐거운", "통합",
    };

    /// <summary>문서 전체에서 시수표를 읽는다. 없으면 빈 목록.</summary>
    public static IReadOnlyList<SubjectHourStandard> Parse(DocumentLayout layout)
    {
        // 과목 → (행 이름 → 값). 여러 쪽에 나뉘어 있어도 과목 이름으로 합친다.
        var merged = new Dictionary<string, Dictionary<string, int>>();
        var order = new List<string>();

        IReadOnlyList<(string Name, double Center)>? columns = null;

        foreach (var page in layout.Pages)
            foreach (var (subject, row, value) in ReadPage(page, ref columns))
            {
                if (!merged.TryGetValue(subject, out var cells))
                {
                    merged[subject] = cells = new Dictionary<string, int>();
                    order.Add(subject);
                }
                cells.TryAdd(row, value);   // 먼저 읽은 쪽을 지킨다 (헤더가 되풀이돼도 안전)
            }

        if (order.Count == 0) return Array.Empty<SubjectHourStandard>();

        return order.Select(subject =>
        {
            var c = merged[subject];
            int V(string key) => c.TryGetValue(key, out var v) ? v : 0;

            // 본교 기준이 따로 없으면 연간 계가 그 자리다
            var school = c.TryGetValue("school", out var s) ? s : V("year");

            return new SubjectHourStandard(
                subject, V("ministry"), V("delta"), school, V("first"), V("second"));
        }).ToList();
    }

    /// <summary>
    /// 한 쪽에서 (과목, 행 이름, 값)들을 읽는다.
    ///
    /// 숫자를 <b>글자 하나씩 열에 배정</b>한다 — 덩어리로 끊으면 칸 사이 간격이 좁은 표에서
    /// "16"이 "1"과 "6"으로 갈리거나 이웃 칸 숫자와 붙는다(스쿨마스터에서 실제로 겪었다).
    /// </summary>
    /// <param name="columns">앞 쪽에서 찾은 열 배치. 이 쪽에 헤더가 없으면 그대로 이어 쓴다
    /// (스쿨마스터는 1학기 행만 있는 쪽이 따로 있다).</param>
    private static IEnumerable<(string Subject, string Row, int Value)> ReadPage(
        DocumentPage page, ref IReadOnlyList<(string Name, double Center)>? columns)
    {
        var lines = page.Lines();
        var headerIndex = FindHeaderLine(lines);

        if (headerIndex >= 0)
        {
            var found = HeaderColumns(lines[headerIndex]);
            if (found.Count >= 3) columns = found;
        }

        if (columns is null) return Array.Empty<(string, string, int)>();

        return ReadRows(lines, headerIndex, columns);
    }

    private static IEnumerable<(string Subject, string Row, int Value)> ReadRows(
        IReadOnlyList<IReadOnlyList<TextGlyph>> lines,
        int headerIndex,
        IReadOnlyList<(string Name, double Center)> columns)
    {
        var slot = columns.Count < 2 ? 30 : (columns[^1].Center - columns[0].Center) / (columns.Count - 1);
        var leftEdge = columns[0].Center - slot * 0.6;

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            var visible = line.Where(g => !g.IsInvisible).OrderBy(g => g.X).ToList();
            if (visible.Count == 0) continue;

            var label = string.Concat(visible.Where(g => g.CenterX < leftEdge).Select(g => g.Text)).Trim();

            var row = RowKey(label);
            if (row is null) continue;

            // 숫자 글자를 가장 가까운 열에 넣고, 열마다 왼쪽부터 이어 붙여 한 수로 읽는다
            var buckets = new string[columns.Count];

            foreach (var g in visible)
            {
                if (g.CenterX < leftEdge) continue;
                if (g.Text.Length != 1 || !char.IsAsciiDigit(g.Text[0])) continue;

                var best = -1;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < columns.Count; i++)
                {
                    var d = Math.Abs(columns[i].Center - g.CenterX);
                    if (d < bestDistance) { bestDistance = d; best = i; }
                }

                // 합계 열은 목록에서 뺐다 — 마지막 과목보다 한참 오른쪽이면 합계 숫자다
                if (best < 0 || bestDistance > slot * 0.7) continue;

                buckets[best] += g.Text;
            }

            for (var i = 0; i < columns.Count; i++)
                if (buckets[i] is { Length: > 0 } text && int.TryParse(text, out var value))
                    yield return (columns[i].Name, row, value);
        }
    }

    /// <summary>과목 이름이 늘어선 줄. 아는 과목 이름이 넷 이상 있으면 그 줄이다.</summary>
    private static int FindHeaderLine(IReadOnlyList<IReadOnlyList<TextGlyph>> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var text = string.Concat(lines[i].Where(g => !g.IsInvisible).Select(g => g.Text));
            if (!text.Contains("국어")) continue;
            if (SubjectHints.Count(text.Contains) >= 4) return i;
        }
        return -1;
    }

    /// <summary>행 이름을 알아본다. 모르는 행이면 null — 표 밖의 줄이다.</summary>
    private static string? RowKey(string label)
    {
        if (label.Length == 0) return null;

        if (label.Contains("교과부")) return "ministry";
        if (label.Contains("증감")) return "delta";
        if (label.Contains("본교기준") || label.Contains("본교 기준")) return "school";
        if (label.Contains("1학기") || label.Contains("１학기")) return "first";
        if (label.Contains("2학기") || label.Contains("２학기")) return "second";
        if (label.Contains("연간")) return "year";
        return null;
    }

    /// <summary>
    /// 헤더 줄을 열로 나눈다.
    ///
    /// <b>간격으로 끊지 않는다</b> — 표가 빽빽하면 "과목국어사회…"가 통째로 한 덩어리가 되어
    /// 열을 하나도 못 잡는다(스쿨마스터·1학년 이지에듀에서 실제로 겪었다).
    /// 대신 아는 이름을 왼쪽부터 맞춰 나가고, 모르는 글자는 한 글자씩 제 열로 둔다.
    /// </summary>
    private static IReadOnlyList<(string Name, double Center)> HeaderColumns(IReadOnlyList<TextGlyph> line)
    {
        var glyphs = line.Where(g => !g.IsInvisible).OrderBy(g => g.X).ToList();
        var text = string.Concat(glyphs.Select(g => g.Text));

        var columns = new List<(string Name, double Center)>();
        var i = 0;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }

            var name = Vocabulary
                .Where(v => i + v.Length <= text.Length && text.AsSpan(i, v.Length).SequenceEqual(v))
                .OrderByDescending(v => v.Length)
                .FirstOrDefault() ?? text[i].ToString();

            var span = glyphs.Skip(i).Take(name.Length).ToList();
            if (span.Count > 0 && !CornerLabels.Contains(name) && !TotalLabels.Contains(name))
                columns.Add((Expand(name), Center(span)));

            i += name.Length;
        }

        // 표 왼쪽에는 "수업일수·교과운영·학교행사·휴일수" 같은 구분 칸이 붙어 있고,
        // 그 글자들이 헤더 줄에 섞여 들어온다. 첫 과목이 나오기 전까지는 버린다.
        var first = columns.FindIndex(c => IsSubjectColumn(c.Name));
        return first <= 0 ? columns : columns.Skip(first).ToList();
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
