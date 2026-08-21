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
        _glyphs = page.Glyphs.Where(g => !g.IsInvisible).ToList();
        _horizontals = page.Rulings.Where(r => !r.Vertical).ToList();

        ColumnEdges = Cluster(page.Rulings.Where(r => r.Vertical).Select(r => r.Position));
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
        var lines = new DocumentPage(0, inside).Lines(tolerance: 4.5).ToList();

        return Merge(lines).Select(Join).Where(t => t.Trim().Length > 0).ToList();
    }

    /// <summary>
    /// <b>작은 기호만 있는 줄을 이웃 줄에 붙인다.</b>
    ///
    /// 가운뎃점 <c>·</c> 은 폭이 0.8pt 에 기준선도 떠 있어(실측) 혼자 한 줄로 잡힌다.
    /// 그대로 두면 좁은 칸에서 <c>듣기·말하기</c> 가 <c>듣·기말하기</c> 로 뒤집힌다 —
    /// 기호 줄이 위 글자와 아래 글자 사이에 끼어들기 때문이다.
    /// 위아래 중 <b>기준선이 더 가까운 쪽</b>에 붙인다.
    /// </summary>
    private static List<IReadOnlyList<TextGlyph>> Merge(List<IReadOnlyList<TextGlyph>> lines)
    {
        if (lines.Count < 2) return lines;

        var normal = lines.SelectMany(l => l).Select(g => g.Width).Where(w => w > 0).ToList();
        var small = (normal.Count > 0 ? normal.Average() : 6) * 0.4;

        var result = new List<IReadOnlyList<TextGlyph>>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var isMarkOnly = line.All(g => g.Width < small);

            if (!isMarkOnly || (result.Count == 0 && i + 1 >= lines.Count))
            {
                result.Add(line);
                continue;
            }

            var y = line.Average(g => g.Y);
            var above = result.Count > 0 ? Math.Abs(result[^1].Average(g => g.Y) - y) : double.MaxValue;
            var below = i + 1 < lines.Count ? Math.Abs(lines[i + 1].Average(g => g.Y) - y) : double.MaxValue;

            if (above <= below && result.Count > 0)
                result[^1] = result[^1].Concat(line).ToList();
            else
                lines[i + 1] = line.Concat(lines[i + 1]).ToList();
        }

        return result;
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
