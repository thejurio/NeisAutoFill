using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 창체 전체 계획 ↔ 세부 계획 병합 (기술설계 §8, D-009, 로드맵 T3).
/// 핵심: 같은 활동을 두 번 세지 않되, 애매하면 지우지 말고 사람에게 넘긴다.
/// </summary>
public class CreativeActivityMergerTests
{
    private static readonly DateOnly 날짜 = new(2026, 9, 16);

    private static CreativeActivityEvent 전체(string name, int? period = null,
        CreativeActivityKind kind = CreativeActivityKind.Autonomy)
        => new(날짜, period, kind, name, CreativeSourceKind.Overall);

    private static CreativeActivityEvent 세부(string name, int? period = null,
        CreativeActivityKind kind = CreativeActivityKind.Autonomy)
        => new(날짜, period, kind, name, CreativeSourceKind.Detail);

    [Fact]
    public void 완전히_같은_일정은_하나로_병합된다()
    {
        var r = CreativeActivityMerger.Merge(new[] { 전체("학급회의", 3), 세부("학급회의", 3) });

        Assert.Single(r.Merged);
        Assert.Equal(2, r.Merged[0].Sources.Count);          // 원본은 둘 다 보존
        Assert.False(r.HasBlockingIssue);
    }

    [Fact]
    public void 병합_대표는_세부_계획이다()
    {
        var r = CreativeActivityMerger.Merge(new[] { 전체("학급회의", 3), 세부("학급회의", 3) });

        Assert.Equal(CreativeSourceKind.Detail, r.Merged[0].Representative.Source);
        Assert.True(r.Merged[0].WasMerged);
        Assert.Contains("병합", r.Merged[0].Describe());
    }

    [Fact]
    public void 활동명_공백_차이는_같은_활동으로_본다()
    {
        var r = CreativeActivityMerger.Merge(new[] { 전체("학급 회의", 3), 세부("학급회의", 3) });

        Assert.Single(r.Merged);
    }

    [Fact]
    public void 한쪽만_교시가_있으면_자동으로_지우지_않고_제안한다()
    {
        var r = CreativeActivityMerger.Merge(new[] { 전체("체험학습"), 세부("체험학습", 2) });

        Assert.Single(r.Merged);
        Assert.Equal(2, r.Merged[0].Representative.Period);   // 구체적인 쪽을 남긴다
        Assert.Single(r.Suggestions);                         // 사용자 확인 대상
        Assert.False(r.HasBlockingIssue);                     // 막지는 않는다
    }

    [Fact]
    public void 교시가_서로_다르면_충돌이다()
    {
        var r = CreativeActivityMerger.Merge(new[] { 전체("체험학습", 2), 세부("체험학습", 5) });

        Assert.Single(r.Conflicts);
        Assert.True(r.HasBlockingIssue);
        Assert.Contains("교시", r.Conflicts[0].Reason);
    }

    [Fact]
    public void 같은_활동인데_창체_종류가_다르면_충돌이다()
    {
        var r = CreativeActivityMerger.Merge(new[]
        {
            전체("동아리 발표", 3, CreativeActivityKind.Autonomy),
            세부("동아리 발표", 3, CreativeActivityKind.Club),
        });

        Assert.Single(r.Conflicts);
        Assert.Contains("종류", r.Conflicts[0].Reason);
        Assert.Empty(r.Merged);          // 사람이 정할 때까지 실행 후보에서 뺀다
        Assert.True(r.HasBlockingIssue);
    }

    [Fact]
    public void 서로_다른_활동은_그대로_남는다()
    {
        var r = CreativeActivityMerger.Merge(new[]
        {
            세부("학급회의", 3, CreativeActivityKind.Autonomy),
            세부("진로 특강", 4, CreativeActivityKind.Career),
        });

        Assert.Equal(2, r.Merged.Count);
        Assert.False(r.HasBlockingIssue);
    }

    [Fact]
    public void 다른_날짜의_같은_활동은_병합하지_않는다()
    {
        var 다음주 = new CreativeActivityEvent(
            날짜.AddDays(7), 3, CreativeActivityKind.Autonomy, "학급회의", CreativeSourceKind.Detail);

        var r = CreativeActivityMerger.Merge(new[] { 세부("학급회의", 3), 다음주 });

        Assert.Equal(2, r.Merged.Count);
    }

    [Fact]
    public void 원본이_없으면_빈_결과다()
    {
        var r = CreativeActivityMerger.Merge(Array.Empty<CreativeActivityEvent>());

        Assert.Empty(r.Merged);
        Assert.False(r.HasBlockingIssue);
    }

    [Fact]
    public void 미분류_창체도_그대로_유지된다()
    {
        // 종류를 모른다고 임의로 자율활동으로 바꾸지 않는다(D-008)
        var r = CreativeActivityMerger.Merge(new[] { 세부("미정 활동", 3, CreativeActivityKind.Unresolved) });

        Assert.Equal(CreativeActivityKind.Unresolved, r.Merged[0].Representative.Kind);
    }
}
