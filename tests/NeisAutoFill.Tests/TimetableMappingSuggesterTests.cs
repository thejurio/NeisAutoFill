using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 자동 제안과 자동 확정 경계 (기술설계 §7·§11, 로드맵 T5).
/// 핵심: 후보가 하나일 때만 자동 확정하고, 복수 교사·창체는 반드시 사람이 정한다.
/// </summary>
public class TimetableMappingSuggesterTests
{
    private static readonly string[] 메뉴 =
    {
        "국어(교사A(account-a))",
        "체육(교사A(account-a))",
        "체육(교사B(account-b))",
        "자율·자치활동(교사A(account-a))",
        "동아리활동(교사B(account-b))",
        "진로활동(교사A(account-a))",
        "창의적 체험활동 심화(교사A(account-a))",
        "취소",
    };

    private static TimetableCatalog 카탈로그 => new(TimetableMenuParser.ParseAll(메뉴));

    private static MappingSuggestion 제안(string raw) =>
        TimetableMappingSuggester.Suggest(TimetableTokenNormalizer.Normalize(raw), 카탈로그);

    [Fact]
    public void 한글자_별칭은_표준_의미로_찾고_자동_확정한다()
    {
        var s = 제안("국");

        Assert.Equal(MatchConfidence.Standard, s.Confidence);
        Assert.Single(s.Candidates);
        Assert.Equal("국어", s.Candidates[0].Subject);
        Assert.True(s.CanAutoConfirm);
    }

    [Fact]
    public void 원문이_그대로_있으면_정확_일치다()
    {
        var s = 제안("국어");

        Assert.Equal(MatchConfidence.Exact, s.Confidence);
        Assert.True(s.CanAutoConfirm);
    }

    [Fact]
    public void 같은_과목에_교사가_여럿이면_자동_확정하지_않는다()
    {
        var s = 제안("체");

        Assert.Equal(2, s.Candidates.Count);
        Assert.False(s.CanAutoConfirm);   // D-006 — 사람이 골라야 한다
    }

    [Fact]
    public void 창은_창체_항목만_후보로_주고_자동_확정하지_않는다()
    {
        var s = 제안("창");

        Assert.All(s.Candidates, o => Assert.Equal(TimetableOptionKind.CreativeActivity, o.Kind));
        Assert.False(s.CanAutoConfirm);          // D-008 — 종류를 임의로 정하지 않는다
        Assert.Contains("창체", s.Describe());
    }

    [Fact]
    public void 창_후보에_일반_과목은_섞이지_않는다()
    {
        var s = 제안("창");

        Assert.DoesNotContain(s.Candidates, o => o.Subject == "국어");
    }

    [Fact]
    public void 사전에_없는_과목은_비슷한_이름을_제안하되_확정하지_않는다()
    {
        var s = 제안("체육대회");   // "체육" 을 포함

        Assert.Equal(MatchConfidence.Similar, s.Confidence);
        Assert.NotEmpty(s.Candidates);
        Assert.False(s.CanAutoConfirm);
    }

    [Fact]
    public void 맞는_항목이_없으면_빈_후보다()
    {
        var s = 제안("한자");

        Assert.Equal(MatchConfidence.None, s.Confidence);
        Assert.Empty(s.Candidates);
        Assert.False(s.CanAutoConfirm);
    }

    [Fact]
    public void 같은_표기가_여러_번_나와도_한_줄로_합친다()
    {
        var tokens = TimetableTokenNormalizer.NormalizeAll(new[] { "국", "국", "체" });

        var list = TimetableMappingSuggester.SuggestAll(tokens, 카탈로그);

        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void 자동_확정은_후보가_하나인_것만_규칙으로_만든다()
    {
        var tokens = TimetableTokenNormalizer.NormalizeAll(new[] { "국", "체", "창", "한자" });
        var suggestions = TimetableMappingSuggester.SuggestAll(tokens, 카탈로그);

        var rules = TimetableMappingSuggester.AutoConfirm(suggestions, 카탈로그.Fingerprint);

        Assert.Single(rules);                 // 국어만
        Assert.Equal("국", rules[0].SourceToken);
        Assert.False(rules[0].IsUserConfirmed);   // 추천일 뿐 사용자 확정은 아니다
        Assert.Equal(카탈로그.Fingerprint, rules[0].CatalogFingerprint);
    }

    [Fact]
    public void 자동_확정한_규칙은_바로_해석에_쓸_수_있다()
    {
        var tokens = TimetableTokenNormalizer.NormalizeAll(new[] { "국" });
        var rules = TimetableMappingSuggester.AutoConfirm(
            TimetableMappingSuggester.SuggestAll(tokens, 카탈로그), 카탈로그.Fingerprint);

        var r = TimetableMappingResolver.Resolve("국", new TimetableCell(new DateOnly(2026, 8, 24), 1), rules);

        Assert.Equal(MappingResolutionKind.Resolved, r.Kind);
    }
}
