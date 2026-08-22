using ClosedXML.Excel;
using NeisAutoFill.Core.Evaluation;

namespace NeisAutoFill.Excel;

/// <summary>
/// 나이스 [평가계획(안)관리 → 일괄업로드]가 받는 엑셀을 만든다.
///
/// 양식은 나이스의 [샘플엑셀다운로드] 파일을 그대로 뜬 것이다(실측 2026-08-21):
/// <b>시트 이름 <c>empty0</c>, 1행에 <c>영역명 │ 성취기준 │ 평가요소</c> 셋뿐.</b>
/// 시트 이름과 머리글이 다르면 나이스가 읽지 못하므로 <b>바꾸지 말 것</b>.
/// </summary>
public static class EvalUploadWorkbook
{
    /// <summary>나이스 양식의 시트 이름 — 샘플 파일에 있던 그대로다.</summary>
    public const string SheetName = "empty0";

    public static readonly string[] Headers = { "영역명", "성취기준", "평가요소" };

    public static void Write(string path, IReadOnlyList<EvalUploadRow> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(SheetName);

        for (var c = 0; c < Headers.Length; c++) ws.Cell(1, c + 1).Value = Headers[c];
        ws.Row(1).Style.Font.SetBold();

        for (var i = 0; i < rows.Count; i++)
        {
            ws.Cell(i + 2, 1).Value = rows[i].Area;
            ws.Cell(i + 2, 2).Value = rows[i].Standard;
            ws.Cell(i + 2, 3).Value = rows[i].Element;
        }

        ws.Column(1).Width = 14;
        ws.Column(2).Width = 60;
        ws.Column(3).Width = 30;

        wb.SaveAs(path);
    }
}
