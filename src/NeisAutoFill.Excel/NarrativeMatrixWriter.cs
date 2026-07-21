using ClosedXML.Excel;

namespace NeisAutoFill.Excel;

/// <summary>
/// 전과목 서술문을 한 시트 매트릭스로 저장 — 학생=행, 과목=열 (ref/양식.xls 형식).
/// 시트명 "교과학습발달상황", 헤더 = 번호 | 이름 | &lt;과목들…&gt;, 셀 = 그 학생·과목 서술문(없으면 빈칸).
/// 과목별 시트로 나누는 <see cref="NarrativeWorkbookWriter"/> 와 대비되는 단일 시트 요약본.
/// </summary>
public static class NarrativeMatrixWriter
{
    public const string SheetName = "교과학습발달상황";

    public static void Write(
        string path,
        IReadOnlyList<string> subjects,
        IReadOnlyList<(string No, string Name, IReadOnlyDictionary<string, string> BySubject)> students)
    {
        if (students.Count == 0 || subjects.Count == 0)
            throw new InvalidOperationException("저장할 서술문이 없습니다. 먼저 생성해 주세요.");

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName);

        // 헤더: 번호 | 이름 | 과목들…
        ws.Cell(1, 1).Value = "번호";
        ws.Cell(1, 2).Value = "이름";
        for (int c = 0; c < subjects.Count; c++)
            ws.Cell(1, 3 + c).Value = subjects[c];
        ws.Row(1).Style.Font.SetBold();

        // 학생마다 한 행 — 과목 열에 해당 서술문(없으면 빈칸)
        int r = 2;
        foreach (var (no, name, bySubject) in students)
        {
            ws.Cell(r, 1).Value = no;
            ws.Cell(r, 2).Value = name;
            for (int c = 0; c < subjects.Count; c++)
            {
                var cell = ws.Cell(r, 3 + c);
                cell.Value = bySubject.TryGetValue(subjects[c], out var text) ? text : "";
                cell.Style.Alignment.SetWrapText();
                cell.Style.Alignment.SetVertical(XLAlignmentVerticalValues.Top);
            }
            r++;
        }

        ws.Column(1).Width = 6;
        ws.Column(2).Width = 12;
        for (int c = 0; c < subjects.Count; c++)
            ws.Column(3 + c).Width = 60;
        ws.SheetView.FreezeRows(1);   // 헤더 고정 — 과목이 많아도 스크롤 시 보이게

        wb.SaveAs(path);
    }
}
