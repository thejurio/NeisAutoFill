using ClosedXML.Excel;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Excel;
using Xunit;

namespace NeisAutoFill.Tests;

public class PlanWorkbookLoaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"plan_{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>DooEval 1단계 표준양식과 같은 형태의 국어 시트 생성 (병합 스타일 캐리포워드 포함).</summary>
    private void WriteStandardPlan()
    {
        using var wb = new XLWorkbook();

        var st = wb.AddWorksheet("학생명단");
        st.Cell(1, 1).Value = "번호"; st.Cell(1, 2).Value = "이름";
        st.Cell(2, 1).Value = 1; st.Cell(2, 2).Value = "홍길동";

        var ws = wb.AddWorksheet("국어");
        string[] header = { "영역", "성취기준", "단원명", "평가방법 평가요소", "수업 방법 연계의 주안점", "평가기준", "평가기준 내용", "평가시기" };
        for (int c = 0; c < header.Length; c++) ws.Cell(1, c + 1).Value = header[c];
        // 잘함 행에만 영역/성취기준, 이후 행은 빈 셀(병합 표기) → 캐리포워드 검증
        ws.Cell(2, 1).Value = "듣기·말하기"; ws.Cell(2, 2).Value = "[6국01-05] 자료를 선별해 발표한다.";
        ws.Cell(2, 6).Value = "잘함"; ws.Cell(2, 7).Value = "핵심 정보를 중심으로 매체를 활용해 발표한다.";
        ws.Cell(3, 6).Value = "보통"; ws.Cell(3, 7).Value = "자료를 선별하여 내용을 구성하고 발표할 수 있다.";
        ws.Cell(4, 6).Value = "노력요함"; ws.Cell(4, 7).Value = "발표 내용을 제한적으로 구성하여 발표한다.";

        // 빈 과목 시트 (헤더만) → 무시돼야 함
        var empty = wb.AddWorksheet("도덕");
        for (int c = 0; c < header.Length; c++) empty.Cell(1, c + 1).Value = header[c];

        wb.SaveAs(_path);
    }

    [Fact]
    public void Parses_domains_and_criteria_with_carry_forward()
    {
        WriteStandardPlan();
        var plans = PlanWorkbookLoader.Load(_path, GradePresets.ThreeLevel);

        var kor = Assert.Single(plans);   // 학생명단·빈 도덕 시트는 제외
        Assert.Equal("국어", kor.SubjectName);
        Assert.Equal(new[] { "듣기·말하기" }, kor.Domains);

        // 세 등급 모두 기준이 잡히고, 성취기준은 캐리포워드로 전 등급에 붙는다
        Assert.Equal(3, kor.Criteria.Count);
        var normal = kor.Criteria[("듣기·말하기", "보통")];
        Assert.Equal("자료를 선별하여 내용을 구성하고 발표할 수 있다.", normal.Text);
        Assert.Equal("[6국01-05] 자료를 선별해 발표한다.", normal.Achievement);
    }

    [Fact]
    public void Ignores_student_roster_and_empty_subject_sheets()
    {
        WriteStandardPlan();
        var plans = PlanWorkbookLoader.Load(_path, GradePresets.ThreeLevel);
        Assert.DoesNotContain(plans, p => p.SubjectName is "학생명단" or "도덕");
    }

    [Fact]
    public void Detects_grades_of_custom_scale()
    {
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("수학");
            ws.Cell(1, 1).Value = "영역"; ws.Cell(1, 2).Value = "성취기준";
            ws.Cell(1, 3).Value = "평가기준"; ws.Cell(1, 4).Value = "평가기준 내용";
            ws.Cell(2, 1).Value = "수와 연산"; ws.Cell(2, 2).Value = "[6수01-01]";
            ws.Cell(2, 3).Value = "상"; ws.Cell(2, 4).Value = "분수 나눗셈을 능숙히 해결한다.";
            ws.Cell(3, 3).Value = "하"; ws.Cell(3, 4).Value = "구체물 도움으로 해결한다.";
            wb.SaveAs(_path);
        }

        // 상/중/하 척도로 읽으면 감지, 3단계 척도로 읽으면 빈 시트 취급
        var plans = PlanWorkbookLoader.Load(_path, GradePresets.SangJungHa);
        var math = Assert.Single(plans);
        Assert.True(math.Criteria.ContainsKey(("수와 연산", "상")));
        Assert.True(math.Criteria.ContainsKey(("수와 연산", "하")));

        Assert.Empty(PlanWorkbookLoader.Load(_path, GradePresets.ThreeLevel));
    }

    [Fact]
    public void Falls_back_to_cell_right_of_grade_when_no_desc_column()
    {
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("사회");
            ws.Cell(1, 1).Value = "영역"; ws.Cell(1, 2).Value = "성취기준"; ws.Cell(1, 3).Value = "평가기준";
            ws.Cell(2, 1).Value = "지리"; ws.Cell(2, 2).Value = "[6사01-01]";
            ws.Cell(2, 3).Value = "잘함"; ws.Cell(2, 4).Value = "지도를 능숙하게 읽는다.";
            wb.SaveAs(_path);
        }

        var plan = Assert.Single(PlanWorkbookLoader.Load(_path, GradePresets.ThreeLevel));
        Assert.Equal("지도를 능숙하게 읽는다.", plan.Criteria[("지리", "잘함")].Text);
    }
}
