using System.Linq;
using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F9 M2 — 전담 담당 등록 → 작업 조합 생성 (순수 로직).</summary>
public class SubjectAssignmentTests
{
    private static SubjectAssignment.GradeEntry G(int grade, string[] classes, string[] subjects) =>
        new() { Grade = grade, Classes = classes.ToList(), Subjects = subjects.ToList() };

    [Fact]
    public void 학년_반_과목_교차로_조합_생성()
    {
        var a = new SubjectAssignment { Grades = { G(3, new[] { "1", "2" }, new[] { "영어" }) } };
        var units = a.BuildUnits();

        Assert.Equal(new[] { "3-1 영어", "3-2 영어" }, units.Select(u => u.Display).ToArray());
    }

    [Fact]
    public void 학년마다_다른_과목_비대칭_지원()
    {
        var a = new SubjectAssignment
        {
            Grades =
            {
                G(3, new[] { "1", "2" }, new[] { "영어" }),
                G(4, new[] { "1" }, new[] { "과학" }),
            },
        };
        Assert.Equal(new[] { "3-1 영어", "3-2 영어", "4-1 과학" },
            a.BuildUnits().Select(u => u.Display).ToArray());
    }

    [Fact]
    public void 제외항목은_조합에서_빠진다()
    {
        var a = new SubjectAssignment
        {
            Grades = { G(3, new[] { "1", "2", "3" }, new[] { "영어" }) },
            Excluded = { "3-2 영어" },   // 2반은 다른 선생님이 → 제외
        };
        Assert.Equal(new[] { "3-1 영어", "3-3 영어" },
            a.BuildUnits().Select(u => u.Display).ToArray());
    }

    [Fact]
    public void 여러과목_교차()
    {
        var a = new SubjectAssignment { Grades = { G(3, new[] { "1" }, new[] { "영어", "과학" }) } };
        Assert.Equal(new[] { "3-1 영어", "3-1 과학" },
            a.BuildUnits().Select(u => u.Display).ToArray());
    }

    [Fact]
    public void 잘못된_학년_반_과목은_무시()
    {
        var a = new SubjectAssignment
        {
            Grades =
            {
                G(9, new[] { "1" }, new[] { "영어" }),        // 학년 범위 밖
                G(3, new[] { "a/b" }, new[] { "영어" }),      // 반 금지문자
                G(3, new[] { "1" }, new[] { "" }),           // 빈 과목
                G(3, new[] { "2" }, new[] { "과학" }),        // 유효
            },
        };
        Assert.Equal(new[] { "3-2 과학" }, a.BuildUnits().Select(u => u.Display).ToArray());
    }

    [Fact]
    public void 중복_조합은_한번만()
    {
        var a = new SubjectAssignment
        {
            Grades =
            {
                G(3, new[] { "1" }, new[] { "영어" }),
                G(3, new[] { "1" }, new[] { "영어" }),   // 중복 등록
            },
        };
        Assert.Single(a.BuildUnits());
    }

    [Fact]
    public void FindByDisplay_현재선택_복원()
    {
        var a = new SubjectAssignment { Grades = { G(3, new[] { "1", "2" }, new[] { "영어" }) } };
        Assert.Equal(new TeachingUnit(3, "2", "영어"), a.FindByDisplay("3-2 영어"));
        Assert.Null(a.FindByDisplay("9-9 없음"));
    }
}
