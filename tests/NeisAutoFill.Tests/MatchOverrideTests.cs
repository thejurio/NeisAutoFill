using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>Phase 5 — 매칭 오버라이드(학생/영역 매핑·제외)와 분석기.</summary>
public class MatchOverrideTests
{
    private static Student St(string no, string name, params (string Area, string Grade)[] grades) =>
        new(no, name, grades.ToDictionary(g => g.Area, g => g.Grade), null);

    private static Dictionary<int, RowMeta> Rows(params (string No, string Name, string Area)[] rows)
    {
        var map = new Dictionary<int, RowMeta>();
        for (int i = 0; i < rows.Length; i++)
            map[i] = new RowMeta(rows[i].No, rows[i].Name, rows[i].Area);
        return map;
    }

    [Fact]
    public void Area_map_redirects_grade_lookup()
    {
        var rowMap = Rows(("1", "홍길동", "쓰기"));
        var students = new[] { St("1", "홍길동", ("글쓰기", "잘함")) };

        var r = StudentMatcher.Build(rowMap, students, GradePresets.ThreeLevel,
            new[] { "글쓰기" }, StudentMatcher.MatchMode.ByName,
            areaMap: new Dictionary<string, string> { ["쓰기"] = "글쓰기" });

        var task = Assert.Single(r.Todo);
        Assert.Equal("잘함", task.TargetGrade);
        Assert.Equal("쓰기", task.Area);   // 리포트는 화면 영역명 유지
    }

    [Fact]
    public void Excluded_area_and_student_are_skipped_with_reason()
    {
        var rowMap = Rows(("1", "홍길동", "읽기"), ("2", "김철수", "읽기"));
        var students = new[] { St("1", "홍길동", ("읽기", "잘함")), St("2", "김철수", ("읽기", "보통")) };

        var r = StudentMatcher.Build(rowMap, students, GradePresets.ThreeLevel,
            new[] { "읽기" }, StudentMatcher.MatchMode.ByName,
            nameMap: new Dictionary<string, string> { ["김철수"] = "" });

        Assert.Single(r.Todo);
        Assert.Contains(r.Skipped, s => s.Name == "김철수" && s.Reason.Contains("사용자 제외"));
    }

    [Fact]
    public void Name_map_redirects_to_other_excel_student()
    {
        var rowMap = Rows(("7", "박서연", "읽기"));
        var students = new[] { St("7", "박서현", ("읽기", "잘함")) };

        var r = StudentMatcher.Build(rowMap, students, GradePresets.ThreeLevel,
            new[] { "읽기" }, StudentMatcher.MatchMode.ByName,
            nameMap: new Dictionary<string, string> { ["박서연"] = "박서현" });

        Assert.Equal("잘함", Assert.Single(r.Todo).TargetGrade);
    }

    [Fact]
    public void Order_mode_skips_blank_positions()
    {
        // 화면 3행 / 엑셀 2영역 — 2번째 위치는 건너뜀
        var rowMap = Rows(("1", "홍길동", "말하기"), ("1", "홍길동", "듣기"), ("1", "홍길동", "쓰기"));
        var students = new[] { St("1", "홍길동", ("말하기", "잘함"), ("쓰기", "보통")) };

        var r = StudentMatcher.Build(rowMap, students, GradePresets.ThreeLevel,
            new[] { "말하기", "", "쓰기" }, StudentMatcher.MatchMode.ByOrder);

        Assert.Null(r.FatalError);
        Assert.Equal(2, r.Todo.Count);
        Assert.Contains(r.Skipped, s => s.Reason.Contains("사용자 제외 (순서)"));
    }

    [Fact]
    public void Analyzer_reports_clean_when_everything_matches()
    {
        var rowMap = Rows(("1", "홍길동", "읽기"));
        var students = new[] { St("1", "홍길동", ("읽기", "잘함")) };
        var issues = MatchAnalyzer.Analyze("국어", "국어", rowMap, students, new[] { "읽기" });
        Assert.True(issues.Clean);
    }

    [Fact]
    public void Analyzer_flags_subject_student_area_and_count_issues()
    {
        var rowMap = Rows(("1", "홍길동", "읽기"), ("2", "박서연", "쓰기"));
        var students = new[] { St("1", "홍길동", ("읽기", "잘함"), ("글쓰기", "보통")) };

        var issues = MatchAnalyzer.Analyze("수학", "국어", rowMap, students, new[] { "읽기", "글쓰기" });

        Assert.True(issues.SubjectMismatch);
        Assert.Contains(("2", "박서연"), issues.UnmatchedStudents);
        Assert.Contains("쓰기", issues.UnmatchedAreas);
        Assert.True(issues.AreaCountMismatch);   // 학생별 화면 1행 ≠ 엑셀 2영역
        Assert.False(issues.Clean);
    }
}
