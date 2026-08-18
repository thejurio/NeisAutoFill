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
    public void 필드_접두사는_요일이_아니라_열_순서다()
    {
        // 실측 2026-08-19: 주차가 수요일에 시작하면 monYmd 에 수요일 날짜가 들어온다.
        // 요일로 믿고 계산하면 엉뚱한 날에 입력하게 된다 — 경고 없이 날짜 그대로 받아들여야 한다.
        var row = new Dictionary<string, string>
        {
            ["pir"] = "1",
            ["monYmd"] = "20260819",   // 수요일
            ["tueYmd"] = "20260820",   // 목요일
        };

        var s = TimetableGridParser.Parse(new[] { row });

        Assert.False(s.HasWarnings);
        Assert.True(s.Cells.ContainsKey(new TimetableCell(new DateOnly(2026, 8, 19), 1)));
        Assert.Equal(1, s.ColumnOf(new DateOnly(2026, 8, 19)));   // 1열
        Assert.Equal(2, s.ColumnOf(new DateOnly(2026, 8, 20)));
    }

    [Fact]
    public void 같은_열에_행마다_다른_날짜가_오면_경고한다()
    {
        // 이건 진짜 이상 신호다 — 화면 해석이 어긋났다는 뜻
        var a = 행(1);
        var b = 행(2); b["monYmd"] = "20260901";

        var s = TimetableGridParser.Parse(new[] { a, b });

        Assert.Contains(s.Warnings, w => w.Contains("행마다 다릅니다"));
    }

    [Fact]
    public void 없는_날짜의_열을_물으면_음수다()
    {
        var s = TimetableGridParser.Parse(new[] { 행(1) });

        Assert.Equal(-1, s.ColumnOf(new DateOnly(2030, 1, 1)));
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

    // ── 2026-08-18 실기기에서 확인된 실제 셀 값 (HTML) ────────────

    [Fact]
    public void 셀_값의_HTML_태그를_걷어낸다()
    {
        var row = 행(1);
        row["monOtpt"] = "<div></div>과학<br/>(교사A(account-a))";

        var s = TimetableGridParser.Parse(new[] { row });

        Assert.Equal("과학\n(교사A(account-a))", s.Cells[new TimetableCell(new DateOnly(2026, 8, 24), 1)]);
    }

    [Fact]
    public void 색상_font_태그가_섞여도_같은_값이_나온다()
    {
        // 실측: 일부 셀은 <font style="color:deeppink;"> 로 감싸여 온다
        var plain = 행(1); plain["monOtpt"] = "<div></div>과학<br/>(교사A(account-a))";
        var pink = 행(1); pink["monOtpt"] = "<div></div>과학<font style=" + '"' + "color:deeppink;" + '"' + "><br/>(교사A(account-a))</font>";

        var cell = new TimetableCell(new DateOnly(2026, 8, 24), 1);
        var a = TimetableGridParser.Parse(new[] { plain });
        var b = TimetableGridParser.Parse(new[] { pink });

        Assert.Equal(a.Cells[cell], b.Cells[cell]);
    }

    [Fact]
    public void HTML_을_걷어낸_셀_값이_메뉴_항목과_같은_안정_키를_만든다()
    {
        var row = 행(1);
        row["monOtpt"] = "<div></div>과학<br/>(교사A(account-a))";

        var s = TimetableGridParser.Parse(new[] { row });
        var current = TimetableMenuParser.Parse(s.Cells[new TimetableCell(new DateOnly(2026, 8, 24), 1)]);
        var target = TimetableMenuParser.Parse("과학(교사A(account-a))");

        Assert.Equal(target.StableKey, current.StableKey);   // 현재 값 ↔ 목표 비교의 전제
    }
}
