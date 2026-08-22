using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 매핑 프로필의 범위·재사용 판단 (기술설계 §13, 로드맵 T5).
/// 핵심: 범위나 카탈로그가 달라지면 저장된 규칙을 자동 실행하지 않는다.
/// </summary>
public class TimetableMappingProfileTests
{
    private static readonly string[] 메뉴 =
    {
        "국어(교사A(account-a))", "체육(교사A(account-a))", "체육(교사B(account-b))", "취소",
    };

    private static TimetableCatalog 카탈로그(IEnumerable<string>? menu = null) =>
        new(TimetableMenuParser.ParseAll((menu ?? 메뉴).ToList()));

    private static TimetableProfileScope 범위(int grade = 5, string cls = "1") =>
        TimetableProfileScope.Create("jbe.neis.go.kr", "학교이름", "사용자ID", 2026, 2, grade, cls);

    private static string 키(string menuText) => TimetableMenuParser.Parse(menuText).StableKey;

    private static TimetableMappingProfile 프로필(TimetableCatalog cat, params TimetableMappingRule[] rules) =>
        new(범위(), cat.Fingerprint, rules, DateTimeOffset.Now);

    [Fact]
    public void 학교와_사용자는_해시로만_저장한다()
    {
        var scope = 범위();

        Assert.DoesNotContain("학교이름", scope.SchoolHash);
        Assert.DoesNotContain("사용자ID", scope.UserHash);
        Assert.Equal(12, scope.SchoolHash.Length);   // 짧은 해시 — 로그에 남겨도 안전(D-012)
    }

    [Fact]
    public void 같은_값이면_같은_해시가_나온다()
    {
        Assert.Equal(범위().SchoolHash, 범위().SchoolHash);
    }

    [Fact]
    public void 범위와_지문이_모두_같으면_재사용한다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat, new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default, true));

        Assert.True(p.CanReuseFor(범위(), cat.Fingerprint));
        Assert.False(p.NeedsRecheck(cat.Fingerprint));
    }

    [Fact]
    public void 다른_반의_프로필은_재사용하지_않는다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat);

        Assert.False(p.CanReuseFor(범위(cls: "2"), cat.Fingerprint));
    }

    [Fact]
    public void 카탈로그가_바뀌면_재확인이_필요하다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat);
        var 바뀐카탈로그 = 카탈로그(메뉴.Where(m => !m.Contains("교사B")));

        Assert.True(p.NeedsRecheck(바뀐카탈로그.Fingerprint));
        Assert.False(p.CanReuseFor(범위(), 바뀐카탈로그.Fingerprint));
    }

    [Fact]
    public void 대상이_사라진_규칙은_살리지_않는다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat,
            new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default, true),
            new TimetableMappingRule("체", 키("체육(교사B(account-b))"), MappingScope.Default, true));

        var 교사B없음 = 카탈로그(메뉴.Where(m => !m.Contains("교사B")));
        var alive = p.RulesStillValid(교사B없음);

        Assert.Single(alive);
        Assert.Equal("국", alive[0].SourceToken);
    }

    /// <summary>
    /// 짝이 그대로면 <b>확정도 그대로</b> — 다시 묻지 않는다 (사용자 요청 2026-08-22).
    /// 목록 어딘가에서 남의 교사가 하나 늘었다는 이유로 붙잡는 것은 쓸데없는 일이다.
    /// </summary>
    [Fact]
    public void 짝이_그대로면_확정도_그대로_둔다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat, new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default, true));

        var alive = p.RulesStillValid(cat);

        Assert.True(alive[0].IsUserConfirmed);
        Assert.Equal(cat.Fingerprint, alive[0].CatalogFingerprint);
    }

    /// <summary>정말 손대야 하는 것 — 짝이 사라진 규칙 수만 센다.</summary>
    [Fact]
    public void 짝이_사라진_규칙만_다시_골라야_할_것으로_센다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat,
            new TimetableMappingRule("국", 키("국어(교사A(account-a))"), MappingScope.Default, true),
            new TimetableMappingRule("체", 키("체육(교사B(account-b))"), MappingScope.Default, true));

        Assert.Equal(0, p.LostCount(cat));
        Assert.Equal(1, p.LostCount(카탈로그(메뉴.Where(m => !m.Contains("교사B")))));
    }

    [Fact]
    public void 입력_안_함_규칙은_대상이_없어도_살아남는다()
    {
        var cat = 카탈로그();
        var p = 프로필(cat, TimetableMappingRule.Skip("창", MappingScope.Default));

        Assert.Single(p.RulesStillValid(cat));
    }
}
