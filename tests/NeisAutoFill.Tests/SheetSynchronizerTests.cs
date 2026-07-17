using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>R1 — 명단·계획 → 성적표 동기화 규칙 (실사용에서 버그났던 시나리오 포함).</summary>
public class SheetSynchronizerTests
{
    private static Student St(string no, string name, params (string A, string G)[] grades) =>
        new(no, name, grades.ToDictionary(g => g.A, g => g.G), null);

    private static SubjectSheet Sheet(params Student[] students) =>
        new("국어", new[] { "듣기", "읽기" }, students);

    private static readonly List<(string, string)> Roster12 = new() { ("1", "김하늘"), ("2", "이준서") };

    [Fact]
    public void 학생_추가시_기존_성적_유지()
    {
        var old = Sheet(St("1", "김하늘", ("듣기", "잘함")));
        var roster = new List<(string, string)> { ("1", "김하늘"), ("2", "이준서") };

        var s = SheetSynchronizer.BuildSheet("국어", old.Areas, old, roster);

        Assert.Equal(2, s.Students.Count);
        Assert.Equal("잘함", s.Students[0].Grades["듣기"]);
        Assert.Empty(s.Students[1].Grades);
    }

    [Fact]
    public void 학생_삭제시_나머지_유지()
    {
        var old = Sheet(St("1", "김하늘", ("듣기", "잘함")), St("2", "이준서", ("듣기", "보통")));
        var roster = new List<(string, string)> { ("2", "이준서") };

        var s = SheetSynchronizer.BuildSheet("국어", old.Areas, old, roster);

        var only = Assert.Single(s.Students);
        Assert.Equal("이준서", only.Name);
        Assert.Equal("보통", only.Grades["듣기"]);
    }

    [Fact]
    public void 번호_변경시_이름으로_성적_이월()
    {
        var old = Sheet(St("5", "김하늘", ("듣기", "잘함")));
        var roster = new List<(string, string)> { ("7", "김하늘") };

        var s = SheetSynchronizer.BuildSheet("국어", old.Areas, old, roster);

        Assert.Equal("7", s.Students[0].No);
        Assert.Equal("잘함", s.Students[0].Grades["듣기"]);
    }

    [Fact]
    public void 영역_개명시_성적이_새_이름으로_따라감()   // ★ 2026-07-17 실사용 버그 재발 방지
    {
        var old = Sheet(St("1", "김하늘", ("듣기", "잘함"), ("읽기", "보통")));
        var newAreas = new[] { "듣기·말하기", "읽기" };   // 듣기 → 듣기·말하기 개명

        var s = SheetSynchronizer.BuildSheet("국어", newAreas, old, Roster12);

        Assert.Equal("잘함", s.Students[0].Grades["듣기·말하기"]);
        Assert.Equal("보통", s.Students[0].Grades["읽기"]);
        Assert.False(s.Students[0].Grades.ContainsKey("듣기"));
    }

    [Fact]
    public void 영역_수가_다르면_개명으로_보지_않고_이름_일치만_이월()
    {
        var old = Sheet(St("1", "김하늘", ("듣기", "잘함"), ("읽기", "보통")));
        var newAreas = new[] { "말하기", "읽기", "쓰기" };   // 2→3개: 모호 → 이름 일치(읽기)만

        var s = SheetSynchronizer.BuildSheet("국어", newAreas, old, Roster12);

        Assert.Equal("보통", s.Students[0].Grades["읽기"]);
        Assert.False(s.Students[0].Grades.ContainsKey("말하기"));
        Assert.Equal("잘함", s.Students[0].Grades["듣기"]);   // 옛 키로 남음 (표에는 안 보이지만 소실 아님)
    }

    [Fact]
    public void 명단_정보가_없으면_기존_학생_유지하고_영역만_반영()
    {
        var old = Sheet(St("1", "김하늘", ("듣기", "잘함")));

        var s = SheetSynchronizer.BuildSheet("국어", new[] { "듣기", "쓰기" }, old,
            new List<(string, string)>(), rosterAuthoritative: false);

        Assert.Single(s.Students);
        Assert.Equal(new[] { "듣기", "쓰기" }, s.Areas);
        Assert.Equal("잘함", s.Students[0].Grades["듣기"]);
    }

    [Fact]
    public void 사용자가_명단을_전부_지우면_학생도_전부_삭제()   // ★ 2026-07-18 실사용 버그 재발 방지
    {
        var old = Sheet(St("1", "김하늘", ("듣기", "잘함")), St("2", "이준서"));

        var s = SheetSynchronizer.BuildSheet("국어", old.Areas, old,
            new List<(string, string)>(), rosterAuthoritative: true);

        Assert.Empty(s.Students);
        Assert.Equal(old.Areas, s.Areas);
    }

    [Fact]
    public void ShapeEquals_는_영역과_명단이_같을때만_참()
    {
        var a = Sheet(St("1", "김하늘"));
        Assert.True(SheetSynchronizer.ShapeEquals(a, Sheet(St("1", "김하늘", ("듣기", "잘함")))));  // 성적 차이는 무관
        Assert.False(SheetSynchronizer.ShapeEquals(a, Sheet(St("2", "김하늘"))));                  // 번호 다름
        Assert.False(SheetSynchronizer.ShapeEquals(a,
            new SubjectSheet("국어", new[] { "듣기" }, new List<Student> { St("1", "김하늘") })));   // 영역 다름
    }

    [Fact]
    public void 개명맵은_같은_위치_이름_교체만_인정()
    {
        Assert.Null(SheetSynchronizer.BuildAreaRenameMap(new[] { "a", "b" }, new[] { "a", "b" }));      // 변화 없음
        Assert.Null(SheetSynchronizer.BuildAreaRenameMap(new[] { "a" }, new[] { "a", "b" }));           // 개수 다름
        Assert.Null(SheetSynchronizer.BuildAreaRenameMap(new[] { "a", "b" }, new[] { "b", "a" }));      // 순서 교환 (옛 이름 존속 → 개명 아님)

        var map = SheetSynchronizer.BuildAreaRenameMap(new[] { "a", "b" }, new[] { "a", "c" });
        Assert.Equal("c", map!["b"]);
    }
}
