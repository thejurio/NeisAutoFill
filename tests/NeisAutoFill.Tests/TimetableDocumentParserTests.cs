using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 연간 시간표 문서(표) 해석 (로드맵 T2 로컬 경로).
/// 좌표만으로 열을 정하는 것이 핵심 — 실제 문서에서 텍스트 평탄화로는 요일이 하루씩 밀렸다.
/// 픽스처는 실제 문서 구조를 본뜬 최소 표다(학교·교사 정보 없음).
/// </summary>
public class TimetableDocumentParserTests
{
    // 표 좌표: 기간 열 X≈110, 교시 칸 150부터 10 간격(월~금 각 3교시), 비고 열 X≈300
    private const double RangeX = 110;
    private const double NoteX = 400;

    private static TextGlyph G(string t, double x, double y) => new(t, x, y, 6);

    /// <summary>교시 헤더 줄 — 1·2·3 이 5번 반복(월~금).</summary>
    private static IEnumerable<TextGlyph> HeaderLine(double y)
    {
        for (var day = 0; day < 5; day++)
            for (var p = 1; p <= 3; p++)
                yield return G(p.ToString(), 150 + (day * 3 + p - 1) * 10, y);

        // '기 간' 과 '비 고' — 파서가 옆 열 위치를 여기서 찾는다
        yield return G("기", RangeX - 8, y);
        yield return G("간", RangeX + 8, y);
        yield return G("비", NoteX, y);
        yield return G("고", NoteX + 10, y);
    }

    /// <summary>주차 한 줄: 기간 + 요일별 토큰(3교시까지) + 비고.</summary>
    private static IEnumerable<TextGlyph> WeekLine(double y, string range, string[] days, string? note = null)
    {
        var x = RangeX - 20;
        foreach (var ch in range) yield return G(ch.ToString(), x += 5, y);

        for (var day = 0; day < days.Length; day++)
            for (var p = 0; p < days[day].Length; p++)
                yield return G(days[day][p].ToString(), 150 + (day * 3 + p) * 10, y);

        if (note is null) yield break;
        var nx = NoteX;
        foreach (var ch in note) yield return G(ch.ToString(), nx += 6, y);
    }

    /// <summary>빨간 글씨(공휴일)를 그 날의 교시 칸에 한 글자씩 깐다. 실제 문서가 그렇게 생겼다.</summary>
    private static IEnumerable<TextGlyph> RedDay(double y, int dayIndex, string label)
    {
        for (var i = 0; i < label.Length && i < 3; i++)
            yield return new TextGlyph(label[i].ToString(), 150 + (dayIndex * 3 + i) * 10, y, 6,
                Red: 1, Green: 0, Blue: 0);
    }

    /// <summary>그림자 글꼴이 찍는 흰 겹 — 눈에 안 보이는 사본이다.</summary>
    private static IEnumerable<TextGlyph> WhiteDay(double y, int dayIndex, string label)
    {
        for (var i = 0; i < label.Length && i < 3; i++)
            yield return new TextGlyph(label[i].ToString(), 150 + (dayIndex * 3 + i) * 10, y, 6,
                Red: 1, Green: 1, Blue: 1);
    }

    private static TimetableSourcePackage Parse(params IEnumerable<TextGlyph>[] lines)
    {
        var glyphs = new List<TextGlyph>
        {
            G("2", 50, 500), G("0", 56, 500), G("2", 62, 500), G("6", 68, 500),
            G("학", 74, 500), G("년", 80, 500), G("도", 86, 500),
            G("1", 95, 500), G("학", 101, 500), G("기", 107, 500),
        };
        foreach (var line in lines) glyphs.AddRange(line);

        return TimetableDocumentParser.Parse(
            new DocumentLayout("test.pdf", new[] { new DocumentPage(1, glyphs) }));
    }

    [Fact]
    public void 헤더에서_학년도와_학기를_읽는다()
    {
        var p = Parse(HeaderLine(400), WeekLine(380, "3.2-3.6", new[] { "국수사" }));

        Assert.Equal(2026, p.SchoolYear);
        Assert.Equal(1, p.Semester);
    }

    [Fact]
    public void 요일_열과_교시_행으로_날짜를_만든다()
    {
        // 2026-03-02 는 월요일
        var p = Parse(HeaderLine(400), WeekLine(380, "3.2-3.6", new[] { "국수사", "영과체" }));

        Assert.Equal("국", p.Lessons.Single(l => l.Cell == new TimetableCell(new DateOnly(2026, 3, 2), 1)).SourceToken);
        Assert.Equal("사", p.Lessons.Single(l => l.Cell == new TimetableCell(new DateOnly(2026, 3, 2), 3)).SourceToken);
        Assert.Equal("영", p.Lessons.Single(l => l.Cell == new TimetableCell(new DateOnly(2026, 3, 3), 1)).SourceToken);
    }

    [Fact]
    public void 학기가_주중에_시작해도_열_위치가_요일을_정한다()
    {
        // 열은 언제나 월~금 고정이고, 학기 시작 전 칸은 비어 있다.
        // 기간이 3.4(수)로 시작해도 3번째 열에 있는 토큰은 수요일이다.
        // 텍스트만 평탄화하면 첫 그룹을 월요일로 오인해 하루씩 밀린다(실제 문서에서 확인한 오류).
        var p = Parse(HeaderLine(400), WeekLine(380, "3.4-3.6", new[] { "", "", "국수사" }));

        var cell = p.Lessons.First().Cell;
        Assert.Equal(new DateOnly(2026, 3, 4), cell.Date);
        Assert.Equal(DayOfWeek.Wednesday, cell.DayOfWeek);
    }

    [Fact]
    public void 비고의_행사를_날짜와_함께_모은다()
    {
        var p = Parse(HeaderLine(400), WeekLine(380, "3.2-3.6", new[] { "국" }, "3.2(월) 시업식"));

        Assert.Contains(p.Events, e => e.Date == new DateOnly(2026, 3, 2) && e.Text.Contains("시업식"));
    }

    [Fact]
    public void 비고_글자가_수업_칸으로_새지_않는다()
    {
        var p = Parse(HeaderLine(400), WeekLine(380, "3.2-3.6", new[] { "국" }, "3.2(월) 재량휴업일"));

        Assert.Single(p.Lessons);
        Assert.Equal("국", p.Lessons[0].SourceToken);
    }

    [Fact]
    public void 학기_범위를_벗어난_기간은_주차로_보지_않는다()
    {
        // 요약표의 숫자가 "12.30-12.31" 처럼 기간 형식으로 걸리는 일이 있다
        var p = Parse(HeaderLine(400),
            WeekLine(380, "3.2-3.6", new[] { "국" }),
            WeekLine(360, "12.30-12.31", new[] { "수" }));

        Assert.Single(p.Lessons);
        Assert.Equal(new DateOnly(2026, 3, 2), p.Lessons[0].Cell.Date);
    }

    [Fact]
    public void 한_칸에_여러_글자가_오면_수업으로_넣지_않고_알린다()
    {
        // "재량휴업일" 처럼 칸을 가로지르는 표기가 한 칸에 쌓인 경우.
        // 2글자까지는 "국어" 같은 정상 표기일 수 있어 버리지 않는다 — 3글자부터 거른다.
        var glyphs = new List<TextGlyph>(HeaderLine(400));
        glyphs.AddRange(WeekLine(380, "3.2-3.6", new[] { "국" }));
        glyphs.Add(G("휴", 150, 380));
        glyphs.Add(G("업", 151, 380));

        var p = TimetableDocumentParser.Parse(new DocumentLayout("t.pdf",
            new[] { new DocumentPage(1, WithHeader(glyphs)) }));

        Assert.Empty(p.Lessons);
        Assert.Contains(p.Warnings, w => w.Contains("수업 표기가 아닌"));
    }

    [Fact]
    public void 교시_헤더가_없으면_아무것도_읽지_않는다()
    {
        var p = Parse(WeekLine(380, "3.2-3.6", new[] { "국수사" }));

        Assert.Empty(p.Lessons);
    }

    private static List<TextGlyph> WithHeader(List<TextGlyph> body)
    {
        var all = new List<TextGlyph>
        {
            G("2", 50, 500), G("0", 56, 500), G("2", 62, 500), G("6", 68, 500),
            G("학", 74, 500), G("년", 80, 500), G("도", 86, 500),
            G("1", 95, 500), G("학", 101, 500), G("기", 107, 500),
        };
        all.AddRange(body);
        return all;
    }
    // ── 공휴일 (빨간 글씨) ──────────────────────────────────

    [Fact]
    public void 빨간_글씨는_공휴일로_읽는다()
    {
        // 실측: 재량휴업일·어린이날·개천절·성탄절이 모두 빨강으로 찍혀 있었다
        var p = Parse(
            HeaderLine(400),
            WeekLine(380, "3.2-3.6", new[] { "국수", "국수", "국수", "국수", "국수" }),
            RedDay(380, 2, "휴업일"));

        Assert.Single(p.HolidayNames);
        Assert.Equal("휴업일", p.HolidayNames.Values.Single());
    }

    [Fact]
    public void 공휴일인_날은_수업을_넣지_않는다()
    {
        var p = Parse(
            HeaderLine(400),
            WeekLine(380, "3.2-3.6", new[] { "국수", "국수", "국수", "국수", "국수" }),
            RedDay(380, 2, "휴업일"));

        var holiday = p.HolidayNames.Keys.Single();

        Assert.DoesNotContain(p.Lessons, l => l.Cell.Date == holiday);
        Assert.Equal(8, p.Lessons.Count);   // 5일 × 2교시 - 공휴일 하루(2칸)
    }

    [Fact]
    public void 공휴일이_없으면_수업이_그대로다()
    {
        var p = Parse(
            HeaderLine(400),
            WeekLine(380, "3.2-3.6", new[] { "국수", "국수", "국수", "국수", "국수" }));

        Assert.Empty(p.HolidayNames);
        Assert.Equal(10, p.Lessons.Count);
    }

    [Fact]
    public void 안_보이는_흰_글씨는_버린다()
    {
        // 한글 그림자 글꼴은 같은 글자를 흰색·빨강·회색 세 겹으로 찍는다.
        // 흰 겹을 그대로 읽으면 "재재재"처럼 겹쳐 나와 경고만 쌓였다.
        var p = Parse(
            HeaderLine(400),
            WeekLine(380, "3.2-3.6", new[] { "국수", "국수", "국수", "국수", "국수" }),
            WhiteDay(380, 0, "휴업일"));

        Assert.Empty(p.Warnings);
        Assert.Equal(10, p.Lessons.Count);   // 흰 글씨가 수업 토큰을 오염시키지 않는다
    }

}
