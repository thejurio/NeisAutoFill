using ClosedXML.Excel;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Excel;

/// <summary>
/// 서식 파일 생성. DooEval Index.html 의 downloadStep1Template /
/// downloadStep2DynamicTemplate 이식 — 샘플 등급은 활성 척도 라벨을 따른다.
/// </summary>
public static class TemplateWriter
{
    private static readonly string[] Step1Subjects =
    {
        "국어", "도덕", "사회", "수학", "과학", "실과", "체육", "음악", "미술", "영어",
        "학교자율시간(과목명변경)",
    };

    private static readonly string[] Step1Header =
    {
        "영역", "성취기준", "단원명", "평가방법 평가요소", "수업 방법 연계의 주안점",
        "평가기준", "평가기준 내용", "평가시기",
    };

    /// <summary>평가계획서 빈 양식: 학생명단 + 10개 과목 + 학교자율시간 시트.</summary>
    public static void WriteStep1Template(string path, GradeScale scale)
    {
        using var wb = new XLWorkbook();

        var roster = wb.AddWorksheet("학생명단");
        roster.Cell(1, 1).Value = "번호";
        roster.Cell(1, 2).Value = "이름";
        string[] samples = { "홍길동", "김철수", "이영희" };
        for (int i = 0; i < samples.Length; i++)
        {
            roster.Cell(i + 2, 1).Value = i + 1;
            roster.Cell(i + 2, 2).Value = samples[i];
        }

        foreach (var subject in Step1Subjects)
        {
            var ws = wb.AddWorksheet(subject);
            for (int c = 0; c < Step1Header.Length; c++)
                ws.Cell(1, c + 1).Value = Step1Header[c];

            // 국어 시트에만 작성 예시 제공 (활성 척도의 단계 수만큼)
            if (subject == "국어")
            {
                int r = 2;
                foreach (var level in scale.Levels)
                {
                    if (r == 2)
                    {
                        ws.Cell(r, 1).Value = "듣기·말하기";
                        ws.Cell(r, 2).Value = "[6국01-05] 자료를 선별해 발표한다.";
                        ws.Cell(r, 3).Value = "3. 발표";
                        ws.Cell(r, 4).Value = "서술형";
                        ws.Cell(r, 5).Value = "프로젝트";
                        ws.Cell(r, 8).Value = "4월";
                    }
                    ws.Cell(r, 6).Value = level.Label;
                    ws.Cell(r, 7).Value = $"({level.Label} 수준의 평가기준 내용을 여기에 작성)";
                    r++;
                }
            }
        }

        wb.SaveAs(path);
    }

    /// <summary>
    /// 성적입력 양식: 평가계획서에서 감지된 과목·영역과 학생명단이 미리 세팅된 파일.
    /// 헤더 = 번호 | 이름 | 영역들... | 과목 특기사항. 성적·특기사항은 공란.
    /// </summary>
    public static void WriteStep2Template(
        string path,
        IReadOnlyList<SubjectPlan> plans,
        IReadOnlyList<(string No, string Name)> roster)
    {
        if (plans.Count == 0)
            throw new InvalidOperationException("평가계획서를 먼저 불러와야 합니다.");
        if (roster.Count == 0)
            throw new InvalidOperationException("평가계획서의 [학생명단] 시트가 비어 있습니다.");

        using var wb = new XLWorkbook();
        foreach (var plan in plans)
        {
            var ws = wb.AddWorksheet(plan.SubjectName);
            ws.Cell(1, 1).Value = "번호";
            ws.Cell(1, 2).Value = "이름";
            for (int d = 0; d < plan.Domains.Count; d++)
                ws.Cell(1, d + 3).Value = plan.Domains[d];
            ws.Cell(1, plan.Domains.Count + 3).Value = "과목 특기사항";

            for (int i = 0; i < roster.Count; i++)
            {
                ws.Cell(i + 2, 1).Value = roster[i].No;
                ws.Cell(i + 2, 2).Value = roster[i].Name;
            }
        }

        wb.SaveAs(path);
    }
}
