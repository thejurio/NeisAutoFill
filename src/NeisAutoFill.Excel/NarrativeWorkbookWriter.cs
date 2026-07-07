using ClosedXML.Excel;

namespace NeisAutoFill.Excel;

/// <summary>생성된 서술문을 과목별 시트(번호|이름|서술문)로 저장.</summary>
public static class NarrativeWorkbookWriter
{
    public static void Write(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<(string No, string Name, string Text)>> bySubject)
    {
        var withData = bySubject.Where(kv => kv.Value.Count > 0).ToList();
        if (withData.Count == 0)
            throw new InvalidOperationException("저장할 서술문이 없습니다. 먼저 생성해 주세요.");

        using var wb = new XLWorkbook();
        foreach (var (subject, rows) in withData)
        {
            var ws = wb.AddWorksheet(subject);
            ws.Cell(1, 1).Value = "번호";
            ws.Cell(1, 2).Value = "이름";
            ws.Cell(1, 3).Value = "서술문";
            ws.Row(1).Style.Font.SetBold();

            int r = 2;
            foreach (var (no, name, text) in rows)
            {
                ws.Cell(r, 1).Value = no;
                ws.Cell(r, 2).Value = name;
                ws.Cell(r, 3).Value = text;
                ws.Cell(r, 3).Style.Alignment.SetWrapText();
                r++;
            }
            ws.Column(1).Width = 6;
            ws.Column(2).Width = 12;
            ws.Column(3).Width = 100;
        }
        wb.SaveAs(path);
    }
}
