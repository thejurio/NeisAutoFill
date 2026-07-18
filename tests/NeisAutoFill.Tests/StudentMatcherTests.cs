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

    // 이름 기반 매칭 (엑셀 영역은 학생 grades 키에서 추출)
    private static StudentMatcher.MatchResult ByName(
        Dictionary<int, RowMeta> rows, Student[] students, GradeScale scale)
    {
        var areas = students.SelectMany(s => s.Grades.Keys).Distinct().ToList();
        return StudentMatcher.Build(rows, students, scale, areas, StudentMatcher.MatchMode.ByName);
    }

    [Fact]
    public void Matches_by_number_and_name_then_builds_todo()
    {
        var rows = Rows((0, "1", "김다예", "문법"), (1, "1", "김다예", "읽기"));
        var students = new[] { S("1", "김다예", ("문법", "잘함"), ("읽기", "보통")) };

        var result = ByName(rows, students, GradePresets.ThreeLevel);

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

        var result = ByName(rows, students, GradePresets.ThreeLevel);

        Assert.Single(result.Todo);
        Assert.Equal("노력요함", result.Todo[0].TargetGrade);
    }

    // ── 동명이인 (U7) ─────────────────────────
    [Fact]
    public void 동명이인은_번호가_맞으면_각각_정확히_매칭()
    {
        var rows = Rows((0, "3", "김민준", "듣기"), (1, "15", "김민준", "듣기"));
        var students = new[]
        {
            S("3", "김민준", ("듣기", "잘함")),
            S("15", "김민준", ("듣기", "노력요함")),
        };
        var result = ByName(rows, students, GradePresets.ThreeLevel);

        Assert.Equal(2, result.Todo.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal("잘함", result.Todo[0].TargetGrade);     // 3번
        Assert.Equal("노력요함", result.Todo[1].TargetGrade);  // 15번
    }

    [Fact]
    public void 동명이인인데_번호가_안맞으면_이름폴백_대신_스킵()
    {
        // 화면 번호(7,8)가 엑셀(3,15)과 안 맞음 → 이름만으로는 누구인지 특정 불가 → 조용히 오입력하지 않고 스킵
        var rows = Rows((0, "7", "김민준", "듣기"), (1, "8", "김민준", "듣기"));
        var students = new[]
        {
            S("3", "김민준", ("듣기", "잘함")),
            S("15", "김민준", ("듣기", "노력요함")),
        };
        var result = ByName(rows, students, GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Equal(2, result.Skipped.Count);
        Assert.All(result.Skipped, s => Assert.Contains("동명이인", s.Reason));
    }

    [Fact]
    public void 동명이인도_확인창_매핑이_있으면_그대로_따른다()
    {
        var rows = Rows((0, "7", "김민준", "듣기"));
        var students = new[]
        {
            S("3", "김민준", ("듣기", "잘함")),
            S("15", "김민준", ("듣기", "노력요함")),
        };
        var areas = new[] { "듣기" };
        // 사용자가 화면 '김민준' → 엑셀에선 이름이 유일하지 않으므로, nameMap 은 이름 문자열 기준
        // (동명이인 매핑은 실무에선 번호 병기 이름을 쓰지만, 여기선 폴백 억제만 검증)
        var nameMap = new Dictionary<string, string> { ["김민준"] = "김민준" };
        var result = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel, areas,
            StudentMatcher.MatchMode.ByName, nameMap: nameMap);

        // 매핑이 있으면 폴백 억제를 우회 — todo 1건 (오입력 스킵이 아님)
        Assert.Single(result.Todo);
    }

    [Fact]
    public void 이름이_유일하면_번호_달라도_이름폴백_유지()
    {
        // 동명이인 아님 → 기존 폴백 동작 그대로 (번호 달라도 이름으로 매칭)
        var rows = Rows((0, "99", "이서준", "듣기"));
        var students = new[] { S("3", "이서준", ("듣기", "보통")) };
        var result = ByName(rows, students, GradePresets.ThreeLevel);

        Assert.Single(result.Todo);
        Assert.Equal("보통", result.Todo[0].TargetGrade);
    }

    [Fact]
    public void Skips_when_student_missing_in_excel()
    {
        var rows = Rows((0, "5", "없는학생", "문법"));
        var result = ByName(rows, Array.Empty<Student>(), GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Single(result.Skipped);
        Assert.Contains("엑셀에 학생 없음", result.Skipped[0].Reason);
    }

    [Fact]
    public void Skips_when_area_value_empty()
    {
        var rows = Rows((0, "1", "김다예", "쓰기"));
        var students = new[] { S("1", "김다예", ("문법", "잘함")) };  // 쓰기 값 없음

        var result = ByName(rows, students, GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Contains("영역값 없음", result.Skipped[0].Reason);
    }

    [Fact]
    public void Skips_grade_outside_active_scale()
    {
        var rows = Rows((0, "1", "김다예", "문법"));
        var students = new[] { S("1", "김다예", ("문법", "상")) };  // 3단계 척도엔 '상' 없음

        var result = ByName(rows, students, GradePresets.ThreeLevel);

        Assert.Empty(result.Todo);
        Assert.Contains("허용외 등급 '상'", result.Skipped[0].Reason);
    }

    [Fact]
    public void Accepts_custom_scale_labels()
    {
        // 상/중/하 척도로 바꾸면 '상'이 유효
        var rows = Rows((0, "1", "김다예", "문법"));
        var students = new[] { S("1", "김다예", ("문법", "상")) };

        var result = ByName(rows, students, GradePresets.SangJungHa);

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

        var result = ByName(rows, students, GradePresets.FiveLevel);

        Assert.Equal(5, result.Todo.Count);
        Assert.Empty(result.Skipped);
    }

    // ── 하이브리드: 문제 감지 + 순서 기반 ──────────

    [Fact]
    public void No_problem_when_area_names_match()
    {
        var rows = Rows((0, "1", "가", "문법"), (1, "1", "가", "읽기"));
        var students = new[] { S("1", "가", ("문법", "잘함"), ("읽기", "보통")) };
        Assert.Null(StudentMatcher.DetectNameProblem(rows, students, new[] { "문법", "읽기" }));
    }

    [Fact]
    public void Detects_unmatched_neis_area()
    {
        var rows = Rows((0, "1", "가", "듣기·말하기"));   // 엑셀엔 '듣기말하기'(가운뎃점 없음)
        var students = new[] { S("1", "가", ("듣기말하기", "잘함")) };
        var p = StudentMatcher.DetectNameProblem(rows, students, new[] { "듣기말하기" });
        Assert.NotNull(p);
        Assert.Contains("듣기·말하기", p);
    }

    [Fact]
    public void Detects_duplicate_neis_area_for_student()
    {
        // 한 학생에게 같은 영역명이 두 행 → 이름 매칭 애매 → 문제
        var rows = Rows((0, "1", "가", "1영역"), (1, "1", "가", "1영역"));
        var students = new[] { S("1", "가", ("1영역", "잘함")) };
        Assert.NotNull(StudentMatcher.DetectNameProblem(rows, students, new[] { "1영역" }));
    }

    [Fact]
    public void Order_mode_aligns_by_position()
    {
        // 엑셀 순서 [A,B,C], 나이스 순서 [B,C,A] (영역명이 안 맞는 상황 가정)
        var rows = Rows(
            (0, "1", "가", "나B"), (1, "1", "가", "나C"), (2, "1", "가", "나A"));
        var students = new[] { S("1", "가", ("A", "잘함"), ("B", "보통"), ("C", "노력요함")) };

        var r = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel,
            new[] { "A", "B", "C" }, StudentMatcher.MatchMode.ByOrder);

        Assert.Null(r.FatalError);
        Assert.Equal(StudentMatcher.MatchMode.ByOrder, r.Mode);
        Assert.Equal(3, r.Todo.Count);
        // 위치 정렬: 행0(idx0)=엑셀[0]=A=잘함, 행1=B=보통, 행2=C=노력요함
        Assert.Equal("잘함", r.Todo[0].TargetGrade);
        Assert.Equal("보통", r.Todo[1].TargetGrade);
        Assert.Equal("노력요함", r.Todo[2].TargetGrade);
        // 로그·리포트엔 NEIS 영역명 사용
        Assert.Equal("나B", r.Todo[0].Area);
    }

    [Fact]
    public void Order_mode_fatal_when_counts_differ()
    {
        // 나이스 2행인데 엑셀 3영역 → 정렬 불가 → FatalError
        var rows = Rows((0, "1", "가", "x"), (1, "1", "가", "y"));
        var students = new[] { S("1", "가", ("A", "잘함"), ("B", "보통"), ("C", "노력요함")) };

        var r = StudentMatcher.Build(rows, students, GradePresets.ThreeLevel,
            new[] { "A", "B", "C" }, StudentMatcher.MatchMode.ByOrder);

        Assert.NotNull(r.FatalError);
        Assert.Contains("다릅니다", r.FatalError);
    }
}
