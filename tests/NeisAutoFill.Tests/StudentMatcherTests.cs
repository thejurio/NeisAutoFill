using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

public class StudentMatcherTests
{
    private static Student S(string no, string name, params (string area, string grade)[] g) =>
        new(no, name, g.ToDictionary(x => x.area, x => x.grade));

    private static Dictionary<int, RowMeta> Rows(params (int idx, string no, string name, string area)[] r) =>
        r.ToDictionary(x => x.idx, x => new RowMeta(x.no, x.name, x.area));

    [Fact]
    public void Matches_by_number_and_name_then_builds_todo()
    {
        var rows = Rows((0, "1", "김다예", "문법"), (1, "1", "김다예", "읽기"));
        var students = new[] { S("1", "김다예", ("문법", "잘함"), ("읽기", "보통")) };

        var result = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel);

        Assert.Equal(2, result.Todo.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal("잘함", result.Todo[0].TargetGrade);
        Assert.Equal("보통", result.Todo[1].TargetGrade);
    }

    [Fact]
    public void Falls_back_to_name_only_when_number_differs()
    {
        // 화면 번호와 엑셀 번호가 달라도 이름으로 매칭
        var rows = Rows((0, "99", "박서연", "듣기·말하기"));
        var students = new[] { S("3", "박서연(전입학)", ("듣기·말하기", "노력요함")) };

        var result = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel);

        Assert.Single(result.Todo);
        Assert.Equal("노력요함", result.Todo[0].TargetGrade);
    }

    [Fact]
    public void Skips_when_student_missing_in_excel()
    {
        var rows = Rows((0, "5", "없는학생", "문법"));
        var result = StudentMatcher.Build(rows, Array.Empty<Student>(), GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Single(result.Skipped);
        Assert.Contains("엑셀에 학생 없음", result.Skipped[0].Reason);
    }

    [Fact]
    public void Skips_when_area_value_empty()
    {
        var rows = Rows((0, "1", "김다예", "쓰기"));
        var students = new[] { S("1", "김다예", ("문법", "잘함")) };  // 쓰기 값 없음

        var result = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Contains("영역값 없음", result.Skipped[0].Reason);
    }

    [Fact]
    public void Skips_grade_outside_active_scale()
    {
        var rows = Rows((0, "1", "김다예", "문법"));
        var students = new[] { S("1", "김다예", ("문법", "상")) };  // 3단계 척도엔 '상' 없음

        var result = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Contains("허용외 등급 '상'", result.Skipped[0].Reason);
    }

    [Fact]
    public void Accepts_custom_scale_labels()
    {
        // 상/중/하 척도로 바꾸면 '상'이 유효
        var rows = Rows((0, "1", "김다예", "문법"));
        var students = new[] { S("1", "김다예", ("문법", "상")) };

        var result = StudentMatcher.Build(rows, students, GradePresets.SangJungHa);

        Assert.Single(result.Todo);
        Assert.Equal("상", result.Todo[0].TargetGrade);
    }

    [Fact]
    public void FiveLevel_scale_all_labels_valid()
    {
        var rows = Rows(
            (0, "1", "가", "A"), (1, "2", "나", "A"), (2, "3", "다", "A"),
            (3, "4", "라", "A"), (4, "5", "마", "A"));
        var students = new[]
        {
            S("1", "가", ("A", "매우잘함")), S("2", "나", ("A", "잘함")),
            S("3", "다", ("A", "보통")), S("4", "라", ("A", "미흡")),
            S("5", "마", ("A", "노력요함")),
        };

        var result = StudentMatcher.Build(rows, students, GradePresets.FiveLevel);

        Assert.Equal(5, result.Todo.Count);
        Assert.Empty(result.Skipped);
    }
}
