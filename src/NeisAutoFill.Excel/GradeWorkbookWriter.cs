using ClosedXML.Excel;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Excel;

/// <summary>편집된 성적을 원본과 같은 형식(번호|이름|영역들|과목 특기사항)으로 저장.</summary>
public static class GradeWorkbookWriter
{
    public static void Write(string path, IReadOnlyList<SubjectSheet> sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var sheet in sheets)
        {
            var ws = wb.AddWorksheet(sheet.SubjectName);
            ws.Cell(1, 1).Value = "번호";
            ws.Cell(1, 2).Value = "이름";
            for (int i = 0; i < sheet.Areas.Count; i++)
                ws.Cell(1, i + 3).Value = sheet.Areas[i];
            int noteCol = sheet.Areas.Count + 3;
            ws.Cell(1, noteCol).Value = "과목 특기사항";
            ws.Row(1).Style.Font.SetBold();

            int r = 2;
            foreach (var st in sheet.Students)
            {
                ws.Cell(r, 1).Value = st.No;
                ws.Cell(r, 2).Value = st.Name;
                for (int i = 0; i < sheet.Areas.Count; i++)
                    ws.Cell(r, i + 3).Value = st.Grades.TryGetValue(sheet.Areas[i], out var g) ? g : "";
                ws.Cell(r, noteCol).Value = st.SpecialNote ?? "";
                r++;
            }
            ws.Column(1).Width = 6;
            ws.Column(2).Width = 12;
            ws.Column(noteCol).Width = 40;
        }
        wb.SaveAs(path);
    }
}
