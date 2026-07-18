using System.Collections.Generic;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F9 M1 — 전담 도메인·경로·조합 조립 (순수 로직).</summary>
public class SubjectModeTests
{
    [Fact]
    public void TeachingUnit_표시명()
    {
        var u = new TeachingUnit(3, "1", "영어");
        Assert.Equal("3-1 영어", u.Display);
        Assert.Equal(new ClassRef(3, "1"), u.ClassRef);
        Assert.Equal("3-1", u.ClassRef.Key);
    }

    [Fact]
    public void 명단은_반별_경로()
    {
        var p = SubjectModePaths.RosterFile(@"C:\ws", new ClassRef(3, "1"));
        Assert.Equal(@"C:\ws\전담\명단\3-1.xlsx", p);
    }

    [Fact]
    public void 평가계획은_학년별_한파일_과목은_시트()
    {
        // 3학년 영어·과학은 같은 파일(3학년.xlsx)의 다른 시트 → 담임 포맷 재사용
        var p = SubjectModePaths.PlanFile(@"C:\ws", 3);
        Assert.Equal(@"C:\ws\전담\평가계획\3학년.xlsx", p);
        // 학년이 다르면 다른 파일
        Assert.NotEqual(p, SubjectModePaths.PlanFile(@"C:\ws", 4));
    }

    [Fact]
    public void 작업은_학년반과목별_폴더()
    {
        var u = new TeachingUnit(3, "1", "영어");
        Assert.Equal(@"C:\ws\전담\작업\3-1_영어", SubjectModePaths.UnitDir(@"C:\ws", u));
        Assert.Equal(@"C:\ws\전담\작업\3-1_영어\성적.xlsx", SubjectModePaths.UnitGradeFile(@"C:\ws", u));
        Assert.Equal(@"C:\ws\전담\작업\3-1_영어\서술문.xlsx", SubjectModePaths.UnitNarrativeFile(@"C:\ws", u));
    }

    [Theory]
    [InlineData("3-1", 3, "1")]
    [InlineData("4-2", 4, "2")]
    [InlineData("3", null, null)]      // 대시 없음
    [InlineData("9-1", null, null)]   // 학년 범위 밖
    [InlineData("3-", null, null)]    // 반 없음
    public void 명단_파일명_파싱_왕복(string name, int? grade, string? cls)
    {
        var c = SubjectModePaths.ParseRosterName(name);
        if (grade is null) Assert.Null(c);
        else { Assert.NotNull(c); Assert.Equal(new ClassRef(grade.Value, cls!), c!.Value); }
    }

    [Fact]
    public void 명단_생성명과_파싱이_왕복()
    {
        var c = new ClassRef(3, "1");
        var fname = System.IO.Path.GetFileNameWithoutExtension(SubjectModePaths.RosterFile(@"C:\ws", c));
        Assert.Equal(c, SubjectModePaths.ParseRosterName(fname));
    }

    [Theory]
    [InlineData("3학년", 3)]
    [InlineData("5학년", 5)]
    [InlineData("9학년", null)]
    [InlineData("영어", null)]
    public void 계획_파일명_파싱(string name, int? grade)
    {
        Assert.Equal(grade, SubjectModePaths.ParsePlanName(name));
    }

    [Fact]
    public void 계획_생성명과_파싱이_왕복()
    {
        var fname = System.IO.Path.GetFileNameWithoutExtension(SubjectModePaths.PlanFile(@"C:\ws", 4));
        Assert.Equal(4, SubjectModePaths.ParsePlanName(fname));
    }

    [Fact]
    public void 담임_전담_경로는_섞이지_않는다()
    {
        // 담임(기본)은 workspaceRoot 직속, 전담은 전담\ 하위 → 충돌 없음
        Assert.StartsWith(@"C:\ws\전담\", SubjectModePaths.RosterFile(@"C:\ws", new ClassRef(3, "1")));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(0, false)]
    [InlineData(7, false)]
    public void 학년_유효성(int grade, bool valid) => Assert.Equal(valid, SubjectModePaths.IsValidGrade(grade));

    [Theory]
    [InlineData("영어", true)]
    [InlineData("학교자율시간", true)]
    [InlineData("a/b", false)]
    [InlineData("", false)]
    public void 과목명_유효성(string? subject, bool valid) =>
        Assert.Equal(valid, SubjectModePaths.IsValidSubject(subject));

    // ── 조합 조립 (명단 + 계획 → 성적표) ──
    private static SubjectPlan Plan(string subject, params string[] areas)
    {
        var crit = new Dictionary<(string, string), CriteriaEntry>();
        return new SubjectPlan(subject, areas, crit);
    }

    [Fact]
    public void 조합_조립_명단순서대로_과목영역으로_성적표()
    {
        var plan = Plan("영어", "듣기", "말하기");
        var roster = new[] { ("1", "김하늘"), ("2", "이바다") };

        var sheet = SheetSynchronizer.BuildUnitSheet(plan, roster);

        Assert.Equal("영어", sheet.SubjectName);
        Assert.Equal(new[] { "듣기", "말하기" }, sheet.Areas);
        Assert.Equal(2, sheet.Students.Count);
        Assert.Equal("김하늘", sheet.Students[0].Name);
        Assert.Equal("2", sheet.Students[1].No);
    }

    [Fact]
    public void 조합_조립_기존성적_이월()
    {
        var plan = Plan("영어", "듣기");
        var roster = new[] { ("1", "김하늘") };
        var old = new SubjectSheet("영어", new[] { "듣기" },
            new List<Student> { new("1", "김하늘", new Dictionary<string, string> { ["듣기"] = "잘함" }) });

        var sheet = SheetSynchronizer.BuildUnitSheet(plan, roster, old);

        Assert.Equal("잘함", sheet.Students[0].Grades["듣기"]);   // 이전 성적 보존
    }
}
