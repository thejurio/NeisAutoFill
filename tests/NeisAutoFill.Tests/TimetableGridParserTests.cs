using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// CLX 시간표 그리드 행 → 셀 주소 변환 (기술설계 §9, 실기기검증 V3·V4, 로드맵 T4).
/// 실측 필드명(pir, {day}Ymd, {day}Otpt)만 사용하고 aria 개수에 의존하지 않는다.
/// 픽스처는 비식별 가상값이다(D-012).
/// </summary>
public class TimetableGridParserTests
{
    /// <summary>2026-08-24(월) ~ 08-28(금) 주의 한 행.</summary>
    private static Dictionary<string, string> 행(int 교시, string? 월 = null, string? 화 = null) =>
        new()
        {
            ["pir"] = 교시.ToString(),
            ["monYmd"] = "20260824", ["monOtpt"] = 월 ?? "",
            ["tueYmd"] = "20260825", ["tueOtpt"] = 화 ?? "",
        };

    [Fact]
    public void 행과_요일_열에서_셀_주소를_만든다()
    {
        var s = TimetableGridParser.Parse(new[] { 행(1, 월: "국어"), 행(2, 월: "수학") });

        Assert.Equal("국어", s.Cells[new TimetableCell(new DateOnly(2026, 8, 24), 1)]);
        Assert.Equal("수학", s.Cells[new TimetableCell(new DateOnly(2026, 8, 24), 2)]);
        Assert.False(s.HasWarnings);
    }

    [Fact]
    public void 빈_셀도_빈_문자열로_담는다()
    {
        // "값이 없는 셀"과 "존재하지 않는 셀"은 다르다 — 전자는 입력 대상이 될 수 있다
        var s = TimetableGridParser.Parse(new[] { 행(1) });

        Assert.Equal("", s.Cells[new TimetableCell(new DateOnly(2026, 8, 24), 1)]);
    }

    [Fact]
    public void 이_주에_있는_날짜를_모은다()
    {
        var s = TimetableGridParser.Parse(new[] { 행(1), 행(2) });

        Assert.Equal(new[] { new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25) }, s.Dates);
    }

    [Fact]
    public void 교시_표기에_글자가_섞여도_읽는다()
    {
        var row = 행(3);
        row["pir"] = "3교시";

        var s = TimetableGridParser.Parse(new[] { row });

        Assert.True(s.Cells.ContainsKey(new TimetableCell(new DateOnly(2026, 8, 24), 3)));
    }

    [Fact]
    public void 교시를_못_읽으면_경고하고_행을_건너뛴다()
    {
        var row = 행(1);
        row["pir"] = "";

        var s = TimetableGridParser.Parse(new[] { row });

        Assert.Empty(s.Cells);
        Assert.Single(s.Warnings);   // 조용히 사라지면 원인을 알 수 없다
    }

    [Fact]
    public void 날짜를_못_읽으면_그_열만_경고한다()
    {
        var row = 행(1, 월: "국어");
        row["tueYmd"] = "이상한값";

        var s = TimetableGridParser.Parse(new[] { row });

        Assert.Single(s.Cells);      // 월요일은 살아남는다
        Assert.Single(s.Warnings);
    }

    [Fact]
    public void 없는_요일_열은_경고하지_않는다()
    {
        // 토·일을 표시하지 않는 화면도 있다 — 정상이다
        var s = TimetableGridParser.Parse(new[] { 행(1, 월: "국어") });

        Assert.False(s.HasWarnings);
    }

    [Fact]
    public void 요일_열과_실제_요일이_어긋나면_경고한다()
    {
        var row = 행(1);
        row["tueYmd"] = "20260824";   // 월요일 날짜가 화요일 열에

        var s = TimetableGridParser.Parse(new[] { row });

        Assert.Contains(s.Warnings, w => w.Contains("요일이 맞지 않습니다"));
    }

    [Fact]
    public void 헤더_날짜와_행_날짜가_같으면_문제가_없다()
    {
        var s = TimetableGridParser.Parse(new[] { 행(1) });

        var problems = TimetableGridParser.Verify(s,
            new[] { new DateOnly(2026, 8, 24), new DateOnly(2026, 8, 25) });

        Assert.Empty(problems);
    }

    [Fact]
    public void 헤더와_행_날짜가_어긋나면_보고한다()
    {
        // 화면 해석이 어긋난 상태 — 이대로 입력하면 엉뚱한 날에 들어간다
        var s = TimetableGridParser.Parse(new[] { 행(1) });

        var problems = TimetableGridParser.Verify(s, new[] { new DateOnly(2026, 8, 31) });

        Assert.Equal(3, problems.Count);   // 행에만 2건 + 헤더에만 1건
    }

    [Fact]
    public void 빈_그리드는_빈_결과다()
    {
        var s = TimetableGridParser.Parse(Array.Empty<Dictionary<string, string>>());

        Assert.Empty(s.Cells);
        Assert.Empty(s.Dates);
        Assert.False(s.HasWarnings);
    }
}
