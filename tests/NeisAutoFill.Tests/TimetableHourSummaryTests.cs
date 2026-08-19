using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 배정 시수 대 기준 시수 (시간표 탭의 시수 표).
/// 실기기 문서에서는 모든 과목이 기준과 정확히 맞았다 — 어긋나면 파서를 의심해야 한다.
/// </summary>
public class TimetableHourSummaryTests
{
    private static readonly DateOnly 월 = new(2026, 3, 2);

    private static IEnumerable<TimetableSourceLesson> 수업(string token, int count) =>
        Enumerable.Range(0, count).Select(i =>
            new TimetableSourceLesson(new TimetableCell(월.AddDays(i / 7), i % 7 + 1), token));

    private static SubjectHourStandard 기준(string subject, int first, int second = 0) =>
        new(subject, first + second, 0, first + second, first, second);

    [Fact]
    public void 배정_칸을_과목별로_센다()
    {
        var rows = TimetableHourSummary.Build(
            수업("국", 5).Concat(수업("수", 3)), Array.Empty<SubjectHourStandard>(), 1);

        Assert.Equal(5, rows.Single(r => r.Subject == "국어").Assigned);
        Assert.Equal(3, rows.Single(r => r.Subject == "수학").Assigned);
    }

    [Fact]
    public void 창체는_한_줄로_합친다()
    {
        // 시수표에는 '창체' 한 칸뿐이다 — 자율·동아리·봉사·진로를 따로 세면 기준과 비교할 수 없다
        var rows = TimetableHourSummary.Build(
            수업("자", 4).Concat(수업("동", 2)).Concat(수업("진", 1)),
            new[] { 기준("창체", 7) }, 1);

        var creative = rows.Single(r => r.Subject == "창체");
        Assert.Equal(7, creative.Assigned);
        Assert.Equal(0, creative.Difference);
    }

    [Fact]
    public void 기준과_배정의_차이를_계산한다()
    {
        var rows = TimetableHourSummary.Build(수업("국", 100), new[] { 기준("국어", 108) }, 1);

        var row = rows.Single(r => r.Subject == "국어");
        Assert.Equal(-8, row.Difference);
        Assert.Equal("-8", row.DifferenceText);
        Assert.False(row.IsBalanced);
    }

    [Fact]
    public void 맞으면_0으로_표시한다()
    {
        var rows = TimetableHourSummary.Build(수업("국", 108), new[] { 기준("국어", 108) }, 1);

        Assert.True(rows.Single(r => r.Subject == "국어").IsBalanced);
        Assert.Equal("0", rows.Single(r => r.Subject == "국어").DifferenceText);
    }

    [Fact]
    public void 학기에_맞는_기준을_쓴다()
    {
        var standards = new[] { 기준("국어", first: 108, second: 96) };

        Assert.Equal(108, TimetableHourSummary.Build(수업("국", 1), standards, 1).Single(r => r.Subject == "국어").Standard);
        Assert.Equal(96, TimetableHourSummary.Build(수업("국", 1), standards, 2).Single(r => r.Subject == "국어").Standard);
    }

    [Fact]
    public void 교사가_고친_기준이_문서보다_우선한다()
    {
        var rows = TimetableHourSummary.Build(
            수업("국", 100), new[] { 기준("국어", 108) }, 1,
            new Dictionary<string, int> { ["국어"] = 100 });

        var row = rows.Single(r => r.Subject == "국어");
        Assert.Equal(100, row.Standard);
        Assert.True(row.StandardIsEdited);
        Assert.Equal(0, row.Difference);
    }

    [Fact]
    public void 문서에_시수표가_없으면_기준을_비워_둔다()
    {
        var row = TimetableHourSummary.Build(수업("국", 5), Array.Empty<SubjectHourStandard>(), 1).Single();

        Assert.Null(row.Standard);
        Assert.Null(row.Difference);
        Assert.Equal("", row.DifferenceText);
        Assert.False(row.IsBalanced);
    }

    [Fact]
    public void 기준만_있고_배정이_없는_과목도_보여_준다()
    {
        // 0시간 배정은 조용히 넘어가면 안 되는 문제다
        var rows = TimetableHourSummary.Build(수업("국", 5), new[] { 기준("도덕", 18) }, 1);

        var moral = rows.Single(r => r.Subject == "도덕");
        Assert.Equal(0, moral.Assigned);
        Assert.Equal(-18, moral.Difference);
    }
}
