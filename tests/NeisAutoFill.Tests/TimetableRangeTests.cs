using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 입력할 기간 고르기 (로드맵 T8).
/// 연간 전체를 한 번에 넣을 필요는 없다 — 원하는 기간만 잘라 나눠서 진행할 수 있어야 한다.
/// </summary>
public class TimetableRangeTests
{
    private static readonly DateOnly 월 = new(2026, 8, 24);

    private static TimetableSourceLesson 수업(int 일후, int 교시 = 1) =>
        new(new TimetableCell(월.AddDays(일후), 교시), "국");

    private static TimetableRangeChoice 범위(int 시작, int 종료, bool 덮어쓰기 = false) =>
        new(월.AddDays(시작), 월.AddDays(종료), 덮어쓰기);

    [Fact]
    public void 시작일과_종료일을_모두_포함한다()
    {
        var r = 범위(0, 4);

        Assert.True(r.Contains(월));
        Assert.True(r.Contains(월.AddDays(4)));
        Assert.False(r.Contains(월.AddDays(5)));
        Assert.False(r.Contains(월.AddDays(-1)));
    }

    [Fact]
    public void 기간_밖의_수업은_걸러낸다()
    {
        var lessons = new[] { 수업(-7), 수업(0), 수업(3), 수업(10) };

        var kept = 범위(0, 4).Filter(lessons);

        Assert.Equal(2, kept.Count);
        Assert.All(kept, l => Assert.InRange(l.Cell.Date, 월, 월.AddDays(4)));
    }

    [Fact]
    public void 하루만_고를_수도_있다()
    {
        var lessons = new[] { 수업(0, 1), 수업(0, 2), 수업(1) };

        Assert.Equal(2, 범위(0, 0).Filter(lessons).Count);
    }

    [Fact]
    public void 덮어쓰기_선택은_그대로_전달된다()
    {
        Assert.True(범위(0, 4, 덮어쓰기: true).AllowOverwrite);
        Assert.False(범위(0, 4).AllowOverwrite);
    }
}
