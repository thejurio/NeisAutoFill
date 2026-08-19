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
    // ── 학년마다 다른 과목 ──────────────────────────────

    [Theory]
    [InlineData("바", "바른 생활")]
    [InlineData("슬", "슬기로운 생활")]
    [InlineData("즐", "즐거운 생활")]
    [InlineData("바생", "바른 생활")]
    [InlineData("즐거운생활", "즐거운 생활")]
    public void 통합교과_표기를_안다(string raw, string expected)
    {
        // 1~2학년은 통합교과(바·슬·즐)를 쓴다. 전국 공통이라 사전에 둔다.
        var token = TimetableTokenNormalizer.Normalize(raw);

        Assert.Equal(expected, token.Standard);
        Assert.True(token.IsKnownAlias);
    }

    [Theory]
    [InlineData("학교자율시간")]
    [InlineData("학교 자율 시간")]
    [InlineData("자율시간")]
    public void 학교자율시간의_정식_이름을_안다(string raw)
    {
        Assert.Equal("학교자율시간", TimetableTokenNormalizer.Normalize(raw).Standard);
    }

    [Fact]
    public void 학교자율시간의_학교별_줄임말은_모르는_표기로_남는다()
    {
        // "융" 처럼 줄이는 글자는 학교마다 다르다. 임의로 짐작하면 엉뚱한 과목이 들어간다 —
        // 표기를 그대로 남겨 두고 사용자가 한 번 이어 주게 한다(그 뒤로는 저장된다).
        var token = TimetableTokenNormalizer.Normalize("융");

        Assert.Equal("융", token.Standard);
        Assert.False(token.IsKnownAlias);
        Assert.False(token.IsCreative);
    }

    [Fact]
    public void 자율시간과_자율자치활동을_섞지_않는다()
    {
        // "자" 는 창체의 자율·자치활동이고, "자율시간" 은 학교자율시간이다 — 다른 것이다.
        Assert.Equal("자율·자치활동", TimetableTokenNormalizer.Normalize("자").Standard);
        Assert.Equal("학교자율시간", TimetableTokenNormalizer.Normalize("자율시간").Standard);
        Assert.True(TimetableTokenNormalizer.Normalize("자").IsCreative);
        Assert.False(TimetableTokenNormalizer.Normalize("자율시간").IsCreative);
    }

    [Fact]
    public void 실과가_없는_학년이어도_표기는_그대로_읽는다()
    {
        // 3~4학년에는 실과가 없다. 그래도 파서가 표기를 버리면 안 된다 —
        // 넣을 수 있는지는 나이스 목록이 정한다(D-001).
        var token = TimetableTokenNormalizer.Normalize("실");

        Assert.Equal("실과", token.Standard);
        Assert.True(token.IsKnownAlias);
    }

}
