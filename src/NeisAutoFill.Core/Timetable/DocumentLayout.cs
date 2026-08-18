namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 문서에서 뽑아낸 글자 하나와 그 위치.
/// 좌표가 있어야 표의 행·열을 되살릴 수 있다 — 텍스트만 뽑으면 칸 경계가 뭉개진다(실측 확인).
/// </summary>
/// <param name="Text">글자</param>
/// <param name="X">왼쪽 좌표 (클수록 오른쪽)</param>
/// <param name="Y">아래쪽 좌표 (<b>클수록 위</b> — PDF 좌표계 그대로)</param>
/// <param name="Width">글자 폭 — 칸 배정은 <b>중심</b>으로 해야 맞는다(한글과 숫자는 폭이 다르다)</param>
public readonly record struct TextGlyph(string Text, double X, double Y, double Width = 0)
{
    /// <summary>글자 중심의 가로 좌표.</summary>
    public double CenterX => X + Width / 2;
}

/// <summary>문서 한 쪽의 글자들. Core 는 이 값만 받고 PDF 라이브러리를 알지 못한다.</summary>
/// <param name="Number">1부터 세는 쪽 번호</param>
public sealed record DocumentPage(int Number, IReadOnlyList<TextGlyph> Glyphs)
{
    /// <summary>
    /// 같은 줄에 있는 글자끼리 묶어 위에서 아래로 돌려준다.
    /// <paramref name="tolerance"/> 는 같은 줄로 볼 Y 간격 — 표 안에서 글자 기준선이 조금씩 흔들린다.
    ///
    /// <b>반올림으로 묶지 않는다.</b> 경계에 걸친 값(100.4 와 101.6)이 서로 다른 줄로 갈려
    /// 실제로 표의 한 행이 여러 조각으로 쪼개졌다 — 간격을 보고 이어 붙인다.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TextGlyph>> Lines(double tolerance = 3.0)
    {
        var sorted = Glyphs.OrderByDescending(g => g.Y).ToList();
        var lines = new List<IReadOnlyList<TextGlyph>>();
        var current = new List<TextGlyph>();
        double? anchor = null;

        foreach (var g in sorted)
        {
            if (anchor is null || Math.Abs(anchor.Value - g.Y) <= tolerance)
            {
                anchor ??= g.Y;
                current.Add(g);
                continue;
            }

            lines.Add(current.OrderBy(x => x.X).ToList());
            current = new List<TextGlyph> { g };
            anchor = g.Y;
        }

        if (current.Count > 0) lines.Add(current.OrderBy(x => x.X).ToList());
        return lines;
    }

    /// <summary>줄 하나를 문자열로 (좌표 순).</summary>
    public static string TextOf(IEnumerable<TextGlyph> line) =>
        string.Concat(line.OrderBy(g => g.X).Select(g => g.Text));
}

/// <summary>문서 전체.</summary>
/// <param name="FileName">파일 이름 — 진단·보고용</param>
public sealed record DocumentLayout(string FileName, IReadOnlyList<DocumentPage> Pages);
