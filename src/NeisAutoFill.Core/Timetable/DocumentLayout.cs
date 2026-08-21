namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 문서에서 뽑아낸 글자 하나와 그 위치.
/// 좌표가 있어야 표의 행·열을 되살릴 수 있다 — 텍스트만 뽑으면 칸 경계가 뭉개진다(실측 확인).
/// </summary>
/// <param name="Text">글자</param>
/// <param name="X">왼쪽 좌표 (클수록 오른쪽)</param>
/// <param name="Y">아래쪽 좌표 (<b>클수록 위</b> — PDF 좌표계 그대로)</param>
/// <param name="Width">글자 폭 — 칸 배정은 <b>중심</b>으로 해야 맞는다(한글과 숫자는 폭이 다르다)</param>
/// <param name="Red">글자색 R (0~1)</param>
/// <param name="Green">글자색 G</param>
/// <param name="Blue">글자색 B</param>
public readonly record struct TextGlyph(
    string Text, double X, double Y, double Width = 0,
    double Red = 0, double Green = 0, double Blue = 0)
{
    /// <summary>글자 중심의 가로 좌표.</summary>
    public double CenterX => X + Width / 2;

    /// <summary>
    /// 빨간 글씨. 이 문서들에서 빨강은 <b>공휴일·휴업일</b> 표시다 (실측 확인 2026-08-19) —
    /// 재량휴업일·어린이날·개천절·한글날·추석·성탄절·신정이 모두 빨강으로 찍혀 있었다.
    /// </summary>
    public bool IsRed => Red > 0.6 && Green < 0.35 && Blue < 0.35;

    /// <summary>
    /// 초록 글씨. 이지에듀는 <b>전담 교사가 맡는 시간</b>을 초록(#008000)으로 찍는다.
    /// 학교에 따라 아예 안 쓸 수도 있다 — 없으면 아무 일도 일어나지 않는다.
    /// </summary>
    public bool IsGreen => Green > 0.3 && Red < 0.4 && Blue < 0.4;

    /// <summary>
    /// 흰 글씨. 한글 문서의 그림자 글꼴은 같은 글자를 <b>흰색·빨강·회색 세 겹</b>으로 찍는다 —
    /// 흰 겹은 눈에 보이지 않는데 그대로 읽으면 "재재재"처럼 세 번 겹쳐 나온다.
    /// </summary>
    public bool IsInvisible => Red > 0.95 && Green > 0.95 && Blue > 0.95;
}

/// <summary>
/// 표를 그린 <b>선</b> 하나 (가로줄 또는 세로줄).
///
/// 왜 필요한가: 평가계획 문서는 표의 칸이 <b>맞붙어 있어</b> 글자만으로는 열을 나눌 수 없다
/// (실측: 빈 세로 구간이 4pt 이상인 곳이 페이지 전체에 두 군데뿐이었다).
/// 선을 읽으면 열·행 경계가 정확히 나오고, <b>세로 병합된 칸</b>도 알아낼 수 있다 —
/// 병합된 칸에는 가로줄이 지나가지 않기 때문이다(실측 2026-08-21).
/// </summary>
/// <param name="Vertical">세로줄인가</param>
/// <param name="Position">세로줄이면 X, 가로줄이면 Y</param>
/// <param name="From">뻗은 구간의 시작 (세로줄이면 아래 Y, 가로줄이면 왼쪽 X)</param>
/// <param name="To">뻗은 구간의 끝</param>
public readonly record struct DocumentRule(bool Vertical, double Position, double From, double To)
{
    public double Length => To - From;
}

/// <summary>문서 한 쪽의 글자들. Core 는 이 값만 받고 PDF 라이브러리를 알지 못한다.</summary>
/// <param name="Number">1부터 세는 쪽 번호</param>
/// <param name="Glyphs">글자와 좌표</param>
/// <param name="Rules">표를 그린 선. 표가 없는 문서에서는 비어 있다</param>
public sealed record DocumentPage(
    int Number,
    IReadOnlyList<TextGlyph> Glyphs,
    IReadOnlyList<DocumentRule>? Rules = null)
{
    /// <summary>표를 그린 선 (없으면 빈 목록).</summary>
    public IReadOnlyList<DocumentRule> Rulings => Rules ?? Array.Empty<DocumentRule>();

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
