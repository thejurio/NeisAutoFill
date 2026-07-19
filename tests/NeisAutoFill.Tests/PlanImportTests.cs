using NeisAutoFill.Core.Scale;
using NeisAutoFill.Generator;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>Phase 3 — AI 평가계획 가져오기 (응답 매핑 + hwpx 추출).</summary>
public class PlanImportTests
{
    [Fact]
    public void Maps_gas_response_to_subject_plans()
    {
        var body = """
        {"ok":true,"subjects":[
          {"subjectName":"국어","domains":[
            {"domainName":"듣기·말하기","achievement":"[6국01-05] 자료를 선별해 발표한다.",
             "criteria":{"잘함":"매체를 활용해 발표한다.","보통":"내용을 구성해 발표한다.","노력요함":"제한적으로 발표한다."}}]},
          {"subjectName":"수학","domains":[
            {"domainName":"수와 연산","achievement":"",
             "criteria":{"잘함":"능숙히 해결한다.","알수없는등급":"버려야 함"}}]}
        ]}
        """;

        var plans = GasPlanImporter.ParseResponse(body, GradePresets.ThreeLevel);

        Assert.Equal(2, plans.Count);
        var kor = plans[0];
        Assert.Equal("국어", kor.SubjectName);
        Assert.Equal(3, kor.Criteria.Count);
        Assert.Equal("[6국01-05] 자료를 선별해 발표한다.", kor.Criteria[("듣기·말하기", "보통")].Achievement);

        var math = plans[1];
        Assert.Single(math.Criteria);                          // 척도에 없는 등급 키는 버림
        Assert.Null(math.Criteria[("수와 연산", "잘함")].Achievement);   // 빈 성취기준 → null
    }

    [Fact]
    public void Throws_helpful_error_on_failure_response()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GasPlanImporter.ParseResponse("""{"ok":false,"error":"사용 가능한 API 키가 없습니다"}""", GradePresets.ThreeLevel));
        Assert.Contains("API 키", ex.Message);

        Assert.Throws<InvalidOperationException>(() =>
            GasPlanImporter.ParseResponse("<html>oops</html>", GradePresets.ThreeLevel));
    }

    // ── F9 M4b: 전담 (학년·과목) 단위 인식 ────────────────────────
    [Fact]
    public void ParseUnits_maps_grade_subject_pairs()
    {
        var body = """
        {"ok":true,"units":[
          {"grade":3,"subject":"영어"},
          {"grade":4,"subject":"과학"},
          {"grade":0,"subject":"음악"}
        ]}
        """;
        var units = GasPlanImporter.ParseUnitsResponse(body);

        Assert.Equal(3, units.Count);
        Assert.Equal(3, units[0].Grade);
        Assert.Equal("영어", units[0].Subject);
        Assert.True(units[0].HasGrade);
        Assert.False(units[2].HasGrade);              // grade 0 = 학년 불명
        Assert.Equal("(학년?) 음악", units[2].Display);
    }

    [Fact]
    public void ParseUnits_drops_창체_and_out_of_range_grade_and_dupes()
    {
        var body = """
        {"ok":true,"units":[
          {"grade":9,"subject":"국어"},
          {"grade":3,"subject":"국어"},
          {"grade":3,"subject":"국어"},
          {"grade":5,"subject":"창의적 체험활동"},
          {"grade":2,"subject":"  "}
        ]}
        """;
        var units = GasPlanImporter.ParseUnitsResponse(body);

        Assert.Equal(2, units.Count);                 // 창체·공백 제외, 중복 제거
        Assert.Equal(0, units[0].Grade);              // 9 → 범위 밖 → 불명(0)
        Assert.Equal("국어", units[0].Subject);
        Assert.Equal(new NeisAutoFill.Core.PlanUnit(3, "국어"), units[1]);
    }

    [Fact]
    public void ParseUnits_empty_on_failure_or_garbage()
    {
        Assert.Empty(GasPlanImporter.ParseUnitsResponse("""{"ok":false,"error":"x"}"""));
        Assert.Empty(GasPlanImporter.ParseUnitsResponse("<html>oops</html>"));
        Assert.Empty(GasPlanImporter.ParseUnitsResponse("""{"ok":true}"""));
    }

    [Fact]
    public void Flatten_section_xml_keeps_table_structure()
    {
        var xml = "<hp:tbl><hp:tr><hp:tc><hp:p><hp:t>영역</hp:t></hp:p></hp:tc>" +
                  "<hp:tc><hp:p><hp:t>성취기준</hp:t></hp:p></hp:tc></hp:tr>" +
                  "<hp:tr><hp:tc><hp:p><hp:t>듣기&amp;말하기</hp:t></hp:p></hp:tc>" +
                  "<hp:tc><hp:p><hp:t>[6국01-05]</hp:t></hp:p></hp:tc></hp:tr></hp:tbl>";

        var text = PlanFileExtractor.FlattenSectionXml(xml);

        Assert.Contains("영역\t", text.Replace("\n\t", "\t"));
        Assert.Contains("듣기&말하기", text);   // 엔티티 복원
        Assert.DoesNotContain("<hp:", text);
    }

    [Fact]
    public void Extracts_text_from_real_hwpx_sample_if_present()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ref", "이지에듀hwpx.hwpx");
        if (!File.Exists(sample)) return;   // 샘플 없는 환경(CI 등)에서는 통과

        var text = PlanFileExtractor.ExtractHwpxText(Path.GetFullPath(sample));
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.True(text.Length > 500, $"추출 텍스트가 너무 짧음: {text.Length}자");
    }
}
