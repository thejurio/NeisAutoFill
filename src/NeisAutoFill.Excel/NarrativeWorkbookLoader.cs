using ClosedXML.Excel;

namespace NeisAutoFill.Excel;

/// <summary>
/// 서술문 엑셀(과목별 시트, 번호|이름|서술문) 읽기 — NarrativeWorkbookWriter 출력과 왕복 호환.
/// 사용자가 엑셀에서 직접 수정한 서술문을 프로그램으로 되가져올 때 사용.
/// </summary>
public static class NarrativeWorkbookLoader
{
    public static IReadOnlyDictionary<string, IReadOnlyList<(string No, string Name, string Text)>> Load(string path)
    {
        using var wb = new XLWorkbook(path);
        var result = new Dictionary<string, IReadOnlyList<(string, string, string)>>();

        foreach (var ws in wb.Worksheets)
        {
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            if (lastRow < 2) continue;

            // 헤더 탐색 (없으면 1|2|3열 순서로 간주)
            int noCol = 1, nameCol = 2, textCol = 3;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 3;
            for (int c = 1; c <= lastCol; c++)
            {
                var h = ws.Cell(1, c).GetString().Trim();
                if (h == "번호") noCol = c;
                else if (h == "이름") nameCol = c;
                else if (h.Contains("서술")) textCol = c;
            }

            var rows = new List<(string, string, string)>();
            for (int r = 2; r <= lastRow; r++)
            {
                var name = ws.Cell(r, nameCol).GetString().Trim();
                var text = ws.Cell(r, textCol).GetString().Trim();
                if (name == "" || text == "") continue;
                rows.Add((ws.Cell(r, noCol).GetString().Trim(), name, text));
            }
            if (rows.Count > 0) result[ws.Name.Trim()] = rows;
        }
        return result;
    }
}
