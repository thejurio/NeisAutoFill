using ClosedXML.Excel;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Excel;

/// <summary>
/// 2단계 성적입력 엑셀 파서. §5 — 시트명=과목명, 헤더 번호/이름/영역들/과목특기사항.
/// 영역 컬럼 판정: {번호, 이름} 제외, "특기" 포함 컬럼 제외한 나머지 전부.
/// 값 자체(등급)는 여기서 검증하지 않고 그대로 읽는다. 검증은 StudentMatcher(활성 척도)에서.
/// </summary>
public static class WorkbookLoader
{
    private static readonly HashSet<string> IgnoreHeaders = new() { "번호", "이름" };

    private static bool IsNoteCol(string? h) => h is not null && h.Contains("특기");

    public static IReadOnlyList<SubjectSheet> Load(string path)
    {
        using var wb = new XLWorkbook(path);
        var result = new List<SubjectSheet>();

        foreach (var ws in wb.Worksheets)
        {
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            if (lastCol == 0 || lastRow < 2) continue;

            var header = new string?[lastCol + 1];
            for (int c = 1; c <= lastCol; c++)
                header[c] = ws.Cell(1, c).GetString().Trim();

            int colNo = IndexOf(header, "번호");
            int colName = IndexOf(header, "이름");
            if (colNo < 0 || colName < 0) continue;   // 명단 시트가 아니면 스킵

            var areas = new List<string>();
            var areaCols = new Dictionary<string, int>();
            for (int c = 1; c <= lastCol; c++)
            {
                var h = header[c];
                if (string.IsNullOrEmpty(h)) continue;
                if (IgnoreHeaders.Contains(h) || IsNoteCol(h)) continue;
                if (!areaCols.ContainsKey(h))
                {
                    areas.Add(h);
                    areaCols[h] = c;
                }
            }

            int colNote = -1;
            for (int c = 1; c <= lastCol; c++)
                if (IsNoteCol(header[c])) { colNote = c; break; }

            var students = new List<Student>();
            for (int r = 2; r <= lastRow; r++)
            {
                var name = ws.Cell(r, colName).GetString().Trim();
                if (string.IsNullOrEmpty(name)) continue;

                var no = ws.Cell(r, colNo).GetString().Trim();
                var grades = new Dictionary<string, string>();
                foreach (var (area, c) in areaCols)
                {
                    var v = ws.Cell(r, c).GetString().Trim();
                    if (!string.IsNullOrEmpty(v)) grades[area] = v;
                }
                var note = colNote > 0 ? ws.Cell(r, colNote).GetString().Trim() : null;

                students.Add(new Student(no, name, grades, string.IsNullOrEmpty(note) ? null : note));
            }

            result.Add(new SubjectSheet(ws.Name.Trim(), areas, students));
        }

        return result;
    }

    private static int IndexOf(string?[] header, string value)
    {
        for (int c = 1; c < header.Length; c++)
            if (header[c] == value) return c;
        return -1;
    }
}
