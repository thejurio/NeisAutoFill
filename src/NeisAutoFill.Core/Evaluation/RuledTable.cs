using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Core.Evaluation;

/// <summary>
/// 선으로 그려진 표 한 쪽을 칸 단위로 되살린다.
///
/// <b>왜 선을 쓰나</b>: 평가계획 문서는 칸이 맞붙어 있어 글자 사이 빈틈으로는 열을 못 나눈다
/// (실측: 한 쪽 전체에서 4pt 이상 빈 세로 구간이 두 군데뿐이었다).
/// 세로줄이 곧 열 경계고, 가로줄이 곧 행 경계다.
///
/// <b>세로 병합</b>도 선으로 알아낸다 — 병합된 칸에는 가로줄이 지나가지 않는다.
/// 이 문서에서는 성취기준 하나가 평가단계 수만큼의 행에 걸치고,
/// 영역·성취기준·평가요소 칸이 그 전체에 걸쳐 병합돼 있다.
/// </summary>
public sealed class RuledTable
{
    private readonly IReadOnlyList<TextGlyph> _glyphs;

    /// <summary>열 경계 X (왼쪽부터). 열 수는 이보다 하나 적다.</summary>
    public IReadOnlyList<double> ColumnEdges { get; }

    /// <summary>행 경계 Y (<b>위에서 아래로</b>). 행 수는 이보다 하나 적다.</summary>
    public IReadOnlyList<double> RowEdges { get; }

    /// <summary>가로줄 — 어디부터 어디까지 뻗었는지가 병합 판정에 쓰인다.</summary>
    private readonly IReadOnlyList<DocumentRule> _horizontals;

    public int ColumnCount => Math.Max(0, ColumnEdges.Count - 1);

    public int RowCount => Math.Max(0, RowEdges.Count - 1);

    /// <summary>같은 선으로 볼 좌표 오차 (pt).</summary>
    private const double Tolerance = 3;

    public RuledTable(DocumentPage page)
    {
        var verticals = page.Rulings.Where(r => r.Vertical).ToList();

        // 표의 위아래는 <b>세로줄이 뻗은 만큼</b>이다. 그 밖의 글자는 쪽 머리글·꼬리말이라
        // 그대로 두면 "2026학년도 … 전주덕일초등학교" 가 자료 행인 척 섞여 든다(실측 5건).
        //
        // <b>끝값을 쓴다 — 넓게 잡고 못 미더운 것은 표시해서 내보낸다.</b>
        // 좁혀 보려고 가운뎃값·최빈값을 써 봤더니 쪽마다 표 높이가 달라 <b>진짜 내용이 잘려 나갔다</b>
        // (성취기준 35건 → 17건). 쪽 꼬리말이 몇 줄 딸려 들어오는 편이 낫다 —
        // 그런 줄은 평가기준이 한 줄뿐이라 "나이스가 모르는 단계 수"로 걸러진다(2026-08-21).
        var top = verticals.Count > 0 ? verticals.Max(r => r.To) : double.MaxValue;
        var bottom = verticals.Count > 0 ? verticals.Min(r => r.From) : double.MinValue;

        _glyphs = page.Glyphs
            .Where(g => !g.IsInvisible && g.Y >= bottom - Tolerance && g.Y <= top + Tolerance)
            .ToList();
        _horizontals = page.Rulings
            .Where(r => !r.Vertical && r.Position >= bottom - Tolerance && r.Position <= top + Tolerance)
            .ToList();

        ColumnEdges = Cluster(verticals.Select(r => r.Position));
        var rows = Cluster(_horizontals.Select(r => r.Position));
        rows.Reverse();   // 위에서 아래로
        RowEdges = rows;
    }

    /// <summary>비슷한 좌표를 하나로 묶는다 — 같은 선이 여러 도형으로 그려지기도 한다.</summary>
    private static List<double> Cluster(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var result = new List<double>();

        foreach (var v in sorted)
        {
            if (result.Count == 0 || v - result[^1] > Tolerance) result.Add(v);
            else result[^1] = (result[^1] + v) / 2;
        }

        return result;
    }

    /// <summary>칸 하나의 글자. 없는 열(-1)이면 빈 문자열 — 양식마다 없는 열이 있다.</summary>
    public string Cell(int row, int column) =>
        column < 0 || column >= ColumnCount || row < 0 || row >= RowCount
            ? ""
            : Text(RowEdges[row + 1], RowEdges[row], ColumnEdges[column], ColumnEdges[column + 1]);

    /// <summary>칸 하나를 <b>줄 단위로</b> — 한 칸에 두 가지가 층으로 들어 있을 때 쓴다.</summary>
    public IReadOnlyList<string> CellLines(int row, int column) =>
        Lines(RowEdges[row + 1], RowEdges[row], ColumnEdges[column], ColumnEdges[column + 1]);

    /// <summary>여러 행에 걸친 칸을 줄 단위로.</summary>
    public IReadOnlyList<string> SpanLines(int firstRow, int lastRow, int column) =>
        Lines(RowEdges[lastRow + 1], RowEdges[firstRow], ColumnEdges[column], ColumnEdges[column + 1]);

    /// <summary>
    /// 여러 행에 걸친 칸의 글자 — <b>세로 병합된 칸</b>을 읽을 때 쓴다.
    /// 병합된 칸의 글자는 가운데 행에 몰려 있지만 <b>이웃 행으로 넘치기도 한다</b>(실측).
    /// 그래서 행 하나씩 읽지 않고 구간 전체를 한 번에 읽는다.
    /// </summary>
    public string Span(int firstRow, int lastRow, int column) =>
        column < 0 || column >= ColumnCount || firstRow < 0 || lastRow >= RowCount
            ? ""
            : Text(RowEdges[lastRow + 1], RowEdges[firstRow], ColumnEdges[column], ColumnEdges[column + 1]);

    /// <summary>
    /// 칸 안의 글자를 하나의 문장으로.
    ///
    /// <b>줄바꿈은 공백이 아니다.</b> 좁은 칸에서는 낱말 한가운데가 잘려 다음 줄로 넘어가므로
    /// (실측: <c>분석하고적</c> / <c>절한표현을</c>), 줄과 줄은 <b>붙여서</b> 잇는다.
    /// 띄어쓰기는 <see cref="Join"/> 이 글자 사이 간격을 보고 되살린다.
    /// </summary>
    private string Text(double bottom, double top, double left, double right)
    {
        // 줄 끝 공백을 <b>지우지 않고</b> 이어 붙인다 — 낱말 경계에서 줄이 바뀌면
        // 그 공백이 유일한 단서다(실측: 지웠더니 "글로쓸", "구성하고,매체를" 처럼 붙어 버렸다).
        var joined = string.Concat(Raw(bottom, top, left, right));

        return System.Text.RegularExpressions.Regex.Replace(joined, @"\s+", " ").Trim();
    }

    private List<string> Lines(double bottom, double top, double left, double right) =>
        Raw(bottom, top, left, right).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    private List<string> Raw(double bottom, double top, double left, double right)
    {
        var inside = _glyphs
            .Where(g =>
            {
                var cx = g.CenterX;
                var cy = g.Y + 2;   // 글자 기준선보다 조금 위를 중심으로 본다
                return cx > left && cx < right && cy > bottom && cy < top;
            })
            .ToList();

        if (inside.Count == 0) return new List<string>();

        // 오차 4.5pt — 같은 줄인데도 글자마다 기준선이 다르다.
        // 실측: [6국04-05] 안에서 '-' 645.9 · '4' 643.5 · '[' 642.3 로 3.7pt 벌어져 있었다.
        // 줄 간격은 8pt 남짓이라 4.5 면 줄끼리 섞이지 않는다.
        var lines = new DocumentPage(0, SnapMarks(inside)).Lines(tolerance: 4.5).ToList();

        return lines.Select(Join).Where(t => t.Trim().Length > 0).ToList();
    }

    /// <summary>
    /// <b>작은 기호의 기준선을 가장 가까운 글자 줄에 맞춘다.</b>
    ///
    /// 가운뎃점 <c>·</c> 은 폭이 0.8pt 이고 <b>기준선이 떠 있다</b>(실측: 아래 글자보다 3.8pt 위).
    /// 그대로 줄을 묶으면 위 줄에 끌려가 <c>듣기·말하기</c> 가 <c>듣·기말하기</c> 로,
    /// <c>실기평가</c> 가 <c>실·기평가</c> 로 뒤집힌다(2026-08-21).
    ///
    /// 좌표를 옮기는 것은 <b>줄을 묶을 때뿐</b>이고, 가로 순서는 원래 X 를 그대로 쓴다.
    /// </summary>
    private static List<TextGlyph> SnapMarks(List<TextGlyph> glyphs)
    {
        var widths = glyphs.Where(g => g.Width > 0).Select(g => g.Width).OrderBy(w => w).ToList();
        if (widths.Count < 3) return glyphs;

        var small = widths[widths.Count / 2] * 0.4;
        var anchors = glyphs.Where(g => g.Width >= small).Select(g => g.Y).Distinct().ToList();
        if (anchors.Count == 0) return glyphs;

        return glyphs
            .Select(g => g.Width >= small
                ? g
                : g with { Y = anchors.OrderBy(y => Math.Abs(y - g.Y)).First() })
            .ToList();
    }

    /// <summary>
    /// 한 줄의 글자를 왼쪽부터 잇는다.
    ///
    /// <b>띄어쓰기는 지어내지 않는다.</b> 이 PDF 들은 공백을 진짜 글자로 갖고 있으므로
    /// (<see cref="!:PdfLayoutExtractor.Extract"/> 의 keepSpaces) 그대로 쓰면 된다.
    /// 글자 사이 간격으로 공백을 추정해 봤더니 <b>양쪽 정렬 때문에 빗나갔다</b> —
    /// 낱말 한가운데가 <c>시 간</c>, <c>경 험</c> 으로 갈라졌다(실측 2026-08-21).
    /// </summary>
    private static string Join(IReadOnlyList<TextGlyph> line) =>
        string.Concat(line.OrderBy(g => g.X).Select(g => g.Text));

    /// <summary>
    /// 이 행의 <b>위쪽 경계에 가로줄이 있는가</b> — 그 열에서.
    /// 없으면 위 행과 <b>세로로 병합된 칸</b>이라는 뜻이다.
    /// </summary>
    public bool HasRuleAbove(int row, int column)
    {
        if (row <= 0) return true;

        var y = RowEdges[row];
        var x = (ColumnEdges[column] + ColumnEdges[column + 1]) / 2;

        return _horizontals.Any(r =>
            Math.Abs(r.Position - y) <= Tolerance && r.From <= x + Tolerance && r.To >= x - Tolerance);
    }

    /// <summary>
    /// <paramref name="column"/> 을 기준으로 행을 묶는다 — 가로줄이 있는 행에서 새 묶음이 시작된다.
    /// 성취기준 하나가 평가단계 수만큼의 행에 걸치는 구조를 그대로 되살린다.
    /// </summary>
    public IReadOnlyList<(int First, int Last)> GroupRows(int column, int firstRow = 0)
    {
        var groups = new List<(int First, int Last)>();

        for (var r = firstRow; r < RowCount; r++)
        {
            if (groups.Count == 0 || HasRuleAbove(r, column)) groups.Add((r, r));
            else groups[^1] = (groups[^1].First, r);
        }

        return groups;
    }
}
