using ClosedXML.Excel;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Excel;

/// <summary>
/// 편집된 평가계획(과목·영역·기준)과 학생명단을 평가계획서 파일로 저장.
/// PlanWorkbookLoader 가 그대로 읽을 수 있는 형식(헤더: 영역|성취기준|평가기준|평가기준 내용)으로 쓴다.
/// </summary>
public static class PlanWorkbookWriter
{
    public static void Write(
        string path,
        IReadOnlyList<SubjectPlan> plans,
        IReadOnlyList<(string No, string Name)> roster,
        GradeScale scale)
    {
        using var wb = new XLWorkbook();

        var rosterWs = wb.AddWorksheet("학생명단");
        rosterWs.Cell(1, 1).Value = "번호";
        rosterWs.Cell(1, 2).Value = "이름";
        rosterWs.Row(1).Style.Font.SetBold();
        for (int i = 0; i < roster.Count; i++)
        {
            rosterWs.Cell(i + 2, 1).Value = roster[i].No;
            rosterWs.Cell(i + 2, 2).Value = roster[i].Name;
        }
        rosterWs.Column(1).Width = 6;
        rosterWs.Column(2).Width = 12;

        foreach (var plan in plans)
        {
            var ws = wb.AddWorksheet(plan.SubjectName);
            ws.Cell(1, 1).Value = "영역";
            ws.Cell(1, 2).Value = "성취기준";
            ws.Cell(1, 3).Value = "평가기준";
            ws.Cell(1, 4).Value = "평가기준 내용";
            ws.Row(1).Style.Font.SetBold();

            int r = 2;
            foreach (var domain in plan.Domains)
            {
                bool firstOfDomain = true;
                foreach (var level in scale.Levels)
                {
                    plan.Criteria.TryGetValue((domain, level.Label), out var entry);
                    // 영역은 각 영역 첫 행에만 (로더가 캐리포워드로 복원).
                    // 성취기준은 매 행에 — 로더의 캐리포워드가 이전 영역 값을 끌고 오지 않도록.
                    if (firstOfDomain) { ws.Cell(r, 1).Value = domain; firstOfDomain = false; }
                    ws.Cell(r, 2).Value = entry?.Achievement ?? "";
                    ws.Cell(r, 3).Value = level.Label;
                    ws.Cell(r, 4).Value = entry?.Text ?? "";
                    r++;
                }
            }
            ws.Column(1).Width = 14;
            ws.Column(2).Width = 34;
            ws.Column(3).Width = 10;
            ws.Column(4).Width = 60;
        }

        wb.SaveAs(path);
    }
}
