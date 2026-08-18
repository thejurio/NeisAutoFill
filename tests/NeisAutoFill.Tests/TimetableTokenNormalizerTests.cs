using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 원본 시간표의 과목 표기 정규화 (기술설계 §7, 로드맵 T1).
/// 표준 의미까지만 확장하고 나이스 과목으로 단정하지 않는다(D-002).
/// </summary>
public class TimetableTokenNormalizerTests
{
    [Theory]
    [InlineData("국", "국어")]
    [InlineData("수", "수학")]
    [InlineData("사", "사회")]
    [InlineData("과", "과학")]
    [InlineData("도", "도덕")]
    [InlineData("실", "실과")]
    [InlineData("체", "체육")]
    [InlineData("음", "음악")]
    [InlineData("미", "미술")]
    [InlineData("영", "영어")]
    public void 한글자_별칭을_표준_의미로_확장한다(string raw, string standard)
    {
        var t = TimetableTokenNormalizer.Normalize(raw);

        Assert.Equal(standard, t.Standard);
        Assert.Equal(raw, t.Raw);            // 원본 표기는 그대로 남는다
        Assert.True(t.IsKnownAlias);
        Assert.False(t.IsCreativeUnresolved);
    }

    [Fact]
    public void 정식_명칭도_그대로_인식한다()
    {
        Assert.Equal("국어", TimetableTokenNormalizer.Normalize("국어").Standard);
    }

    [Theory]
    [InlineData("창")]
    [InlineData("창체")]
    [InlineData("(창)")]
    [InlineData("창의적 체험활동")]
    [InlineData("창의적체험활동")]
    public void 창_표기는_미분류_창체로_유지한다(string raw)
    {
        var t = TimetableTokenNormalizer.Normalize(raw);

        Assert.True(t.IsCreativeUnresolved);                          // 자율/동아리/진로로 단정 금지(D-008)
        Assert.Equal(TimetableTokenNormalizer.CreativeStandard, t.Standard);
        Assert.Equal(raw, t.Raw);
    }

    [Fact]
    public void 사전에_없는_학교_과목은_그대로_보존한다()
    {
        var t = TimetableTokenNormalizer.Normalize("한자");

        Assert.Equal("한자", t.Standard);     // 버리지 않는다 — 버리면 매핑 기회가 사라진다
        Assert.False(t.IsKnownAlias);
        Assert.False(t.IsCreativeUnresolved);
    }

    [Fact]
    public void 감싼_괄호는_벗기되_원문은_유지한다()
    {
        var t = TimetableTokenNormalizer.Normalize("(국)");

        Assert.Equal("국어", t.Standard);
        Assert.Equal("(국)", t.Raw);
    }

    [Fact]
    public void 앞뒤_공백은_무시한다()
    {
        Assert.Equal("수학", TimetableTokenNormalizer.Normalize("  수  ").Standard);
    }

    [Fact]
    public void 빈_표기는_빈_토큰이다()
    {
        var t = TimetableTokenNormalizer.Normalize("   ");

        Assert.Equal("", t.Raw);
        Assert.Equal("", t.Standard);
        Assert.False(t.IsCreativeUnresolved);
    }

    [Fact]
    public void 여러_표기를_순서대로_변환한다()
    {
        var list = TimetableTokenNormalizer.NormalizeAll(new[] { "국", "창", "한자" });

        Assert.Equal(new[] { "국어", "창의적 체험활동", "한자" }, list.Select(t => t.Standard));
    }
}
