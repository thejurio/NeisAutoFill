using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 시간표의 창체 칸 ↔ 창체 계획 연결 (기술설계 §8, 로드맵 T3).
/// 핵심: 근거가 있으면 정하고, 갈리면 임의로 고르지 않는다.
/// </summary>
public class CreativeActivityLinkerTests
{
    private static readonly DateOnly 날 = new(2026, 3, 16);

    private static TimetableSourceLesson 칸(string token, int period = 3) =>
        new(new TimetableCell(날, period), token);

    private static MergedCreativeActivity 계획(CreativeActivityKind kind, string name, DateOnly? date = null)
    {
        var e = new CreativeActivityEvent(date ?? 날, null, kind, name, CreativeSourceKind.Detail);
        return new MergedCreativeActivity(e, new[] { e });
    }

    [Fact]
    public void 원본에_종류가_적혀_있으면_그대로_쓴다()
    {
        var links = CreativeActivityLinker.Link(new[] { 칸("동") }, Array.Empty<MergedCreativeActivity>());

        Assert.Single(links);
        Assert.Equal(CreativeActivityKind.Club, links[0].Kind);
        Assert.Equal(CreativeLinkStatus.FromSource, links[0].Status);
        Assert.True(links[0].IsResolved);
    }

    [Fact]
    public void 원본_종류에_맞는_계획이_있으면_활동명까지_붙는다()
    {
        var links = CreativeActivityLinker.Link(
            new[] { 칸("동") }, new[] { 계획(CreativeActivityKind.Club, "동아리 운영(1)") });

        Assert.Equal("동아리 운영(1)", links[0].ActivityName);
        Assert.Contains("동아리 운영(1)", links[0].Reason);
    }

    [Fact]
    public void 미분류_창은_그_날_계획에서_종류를_가져온다()
    {
        var links = CreativeActivityLinker.Link(
            new[] { 칸("창") }, new[] { 계획(CreativeActivityKind.Career, "진로 연계 교육") });

        Assert.Equal(CreativeActivityKind.Career, links[0].Kind);
        Assert.Equal(CreativeLinkStatus.FromPlan, links[0].Status);
        Assert.Equal("진로 연계 교육", links[0].ActivityName);
    }

    [Fact]
    public void 그_날_계획에_종류가_여럿이면_임의로_고르지_않는다()
    {
        // 계획에는 교시가 없어 어느 칸이 무엇인지 알 수 없다
        var links = CreativeActivityLinker.Link(new[] { 칸("창") }, new[]
        {
            계획(CreativeActivityKind.Autonomy, "학교폭력 예방교육"),
            계획(CreativeActivityKind.Career, "진로 특강"),
        });

        Assert.Equal(CreativeLinkStatus.Conflict, links[0].Status);
        Assert.Equal(CreativeActivityKind.Unresolved, links[0].Kind);
        Assert.False(links[0].IsResolved);
    }

    [Fact]
    public void 원본과_계획이_어긋나면_충돌이다()
    {
        var links = CreativeActivityLinker.Link(
            new[] { 칸("동") }, new[] { 계획(CreativeActivityKind.Career, "진로 특강") });

        Assert.Equal(CreativeLinkStatus.Conflict, links[0].Status);
        Assert.Contains("계획에는", links[0].Reason);
    }

    [Fact]
    public void 그_날_계획이_없으면_미해결이다()
    {
        var links = CreativeActivityLinker.Link(new[] { 칸("창") }, Array.Empty<MergedCreativeActivity>());

        Assert.Equal(CreativeLinkStatus.Unresolved, links[0].Status);
        Assert.Contains("계획이 없습니다", links[0].Reason);
    }

    [Fact]
    public void 다른_날_계획은_쓰지_않는다()
    {
        var links = CreativeActivityLinker.Link(
            new[] { 칸("창") }, new[] { 계획(CreativeActivityKind.Club, "동아리", 날.AddDays(7)) });

        Assert.Equal(CreativeLinkStatus.Unresolved, links[0].Status);
    }

    [Fact]
    public void 일반_과목은_결과에_넣지_않는다()
    {
        var links = CreativeActivityLinker.Link(
            new[] { 칸("국"), 칸("창", 4) }, Array.Empty<MergedCreativeActivity>());

        Assert.Single(links);
        Assert.Equal(4, links[0].Cell.Period);
    }

    [Fact]
    public void 봉사활동은_나이스에_없어_미분류로_남는다()
    {
        // '봉'은 원본에 있지만 나이스 메뉴에는 자율·자치/동아리/진로뿐이다
        var links = CreativeActivityLinker.Link(new[] { 칸("봉") }, Array.Empty<MergedCreativeActivity>());

        Assert.Equal(CreativeActivityKind.Unresolved, links[0].Kind);
        Assert.False(links[0].IsResolved);
    }

    [Fact]
    public void 같은_날_창체_칸이_여럿이어도_각각_판단한다()
    {
        var links = CreativeActivityLinker.Link(
            new[] { 칸("자", 3), 칸("동", 5) },
            new[] { 계획(CreativeActivityKind.Autonomy, "학급 다모임"), 계획(CreativeActivityKind.Club, "동아리 운영") });

        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(CreativeLinkStatus.FromSource, l.Status));
        Assert.Equal("학급 다모임", links[0].ActivityName);
        Assert.Equal("동아리 운영", links[1].ActivityName);
    }

    // ── 두 문서가 짝이 맞는지 ──────────────────────────────────

    private static TimetableSourcePackage 시간표(int year, int semester, params DateOnly[] dates) =>
        new(year, semester,
            dates.Select(d => new TimetableSourceLesson(new TimetableCell(d, 1), "창")).ToList(),
            Array.Empty<(DateOnly, string)>(), Array.Empty<string>());

    private static CreativeSourcePackage 창체(int year, int semester, params DateOnly[] dates) =>
        new(year, semester,
            dates.Select(d => new CreativeActivityEvent(
                d, null, CreativeActivityKind.Autonomy, "활동", CreativeSourceKind.Detail)).ToList(),
            Array.Empty<string>());

    [Fact]
    public void 학년도가_다르면_알려준다()
    {
        var problems = CreativeActivityLinker.CheckPair(
            시간표(2025, 1, 날), 창체(2026, 1, 날));

        Assert.Contains(problems, p => p.Contains("학년도가 다릅니다"));
    }

    [Fact]
    public void 학기가_다르면_알려준다()
    {
        var problems = CreativeActivityLinker.CheckPair(
            시간표(2026, 1, 날), 창체(2026, 2, 날));

        Assert.Contains(problems, p => p.Contains("학기가 다릅니다"));
    }

    [Fact]
    public void 창체_일정이_모두_시간표_기간_밖이면_알려준다()
    {
        var problems = CreativeActivityLinker.CheckPair(
            시간표(2026, 1, 날), 창체(2026, 1, 날.AddYears(1)));

        Assert.Contains(problems, p => p.Contains("모두 시간표 기간"));
    }

    [Fact]
    public void 짝이_맞으면_문제가_없다()
    {
        var problems = CreativeActivityLinker.CheckPair(
            시간표(2026, 1, 날), 창체(2026, 1, 날));

        Assert.Empty(problems);
    }
}
