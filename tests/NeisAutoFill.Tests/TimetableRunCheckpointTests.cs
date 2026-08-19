using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Tests;

/// <summary>
/// 연간 입력의 재개 기록 (기술설계 §12, 로드맵 T8).
/// 여기서 지키려는 것은 하나다 — <b>끝났다고 잘못 적으면 안 된다.</b>
/// </summary>
public class TimetableRunCheckpointTests
{
    private static readonly DateOnly 월 = new(2026, 8, 24);
    private static readonly DateTimeOffset 지금 = new(2026, 8, 19, 10, 0, 0, TimeSpan.FromHours(9));

    private static TimetableProfileScope 범위(string 반 = "1") =>
        TimetableProfileScope.Create("jbe.neis.go.kr", "학교", "사용자", 2026, 2, 3, 반);

    private static TimetableRunCheckpoint 새기록 =>
        TimetableRunCheckpoint.Start(범위(), "plan-a", "cat-a", 지금);

    [Fact]
    public void 처음에는_완료된_주가_없다()
    {
        Assert.False(새기록.IsCompleted(월));
        Assert.Equal("완료된 주 없음", 새기록.Describe());
    }

    [Fact]
    public void 완료한_주를_기억한다()
    {
        var c = 새기록.WithWeekDone(월, 지금);

        Assert.True(c.IsCompleted(월));
        Assert.False(c.IsCompleted(월.AddDays(7)));
    }

    [Fact]
    public void 같은_주를_두_번_넣어도_한_번만_남는다()
    {
        var c = 새기록.WithWeekDone(월, 지금).WithWeekDone(월, 지금);

        Assert.Single(c.CompletedWeeks);
    }

    [Fact]
    public void 완료된_주는_날짜순으로_정렬한다()
    {
        var c = 새기록.WithWeekDone(월.AddDays(14), 지금).WithWeekDone(월, 지금);

        Assert.Equal(new[] { 월, 월.AddDays(14) }, c.CompletedWeeks);
    }

    [Fact]
    public void 같은_계획_같은_목록이면_이어서_한다()
    {
        Assert.True(새기록.CanResume(범위(), "plan-a", "cat-a"));
        Assert.Null(새기록.ResumeBlocker(범위(), "plan-a", "cat-a"));
    }

    [Fact]
    public void 원본이_바뀌면_이어서_하지_않는다()
    {
        // 문서를 다시 뽑아 왔는데 내용이 달라졌다면, 끝냈다고 적힌 주가 지금 계획과 다를 수 있다
        Assert.False(새기록.CanResume(범위(), "plan-b", "cat-a"));
        Assert.Contains("원본", 새기록.ResumeBlocker(범위(), "plan-b", "cat-a"));
    }

    [Fact]
    public void 나이스_목록이_바뀌면_이어서_하지_않는다()
    {
        Assert.Contains("과목·교사 목록", 새기록.ResumeBlocker(범위(), "plan-a", "cat-b"));
    }

    [Fact]
    public void 다른_반의_기록은_쓰지_않는다()
    {
        Assert.Contains("다른 학급", 새기록.ResumeBlocker(범위("2"), "plan-a", "cat-a"));
    }

    [Fact]
    public void 계획_지문은_정렬에_독립적이다()
    {
        var a = new TimetableAssignment(new TimetableCell(월, 1), "국", AssignmentStatus.Pending, "L|국어|h|k");
        var b = new TimetableAssignment(new TimetableCell(월, 2), "체", AssignmentStatus.Pending, "L|체육|h|k");

        Assert.Equal(
            TimetableRunCheckpoint.FingerprintOf(new[] { a, b }),
            TimetableRunCheckpoint.FingerprintOf(new[] { b, a }));
    }

    [Fact]
    public void 대상이_달라지면_계획_지문도_달라진다()
    {
        var a = new TimetableAssignment(new TimetableCell(월, 1), "국", AssignmentStatus.Pending, "L|국어|h|k");
        var 다른교사 = a with { TargetStableKey = "L|국어|h2|k2" };

        Assert.NotEqual(
            TimetableRunCheckpoint.FingerprintOf(new[] { a }),
            TimetableRunCheckpoint.FingerprintOf(new[] { 다른교사 }));
    }

    [Fact]
    public void 분류가_달라져도_계획_지문은_그대로다()
    {
        // 화면에 이미 값이 들어가면 Pending 이 AlreadyMatches 로 바뀐다.
        // 그때마다 지문이 달라지면 재개가 영영 안 된다 — 계획이 바뀐 게 아니다.
        var a = new TimetableAssignment(new TimetableCell(월, 1), "국", AssignmentStatus.Pending, "L|국어|h|k");
        var 입력됨 = a with { Status = AssignmentStatus.AlreadyMatches, CurrentStableKey = "L|국어|h|k" };

        Assert.Equal(
            TimetableRunCheckpoint.FingerprintOf(new[] { a }),
            TimetableRunCheckpoint.FingerprintOf(new[] { 입력됨 }));
    }

    [Fact]
    public void 계획에서_바로_지문을_얻는다()
    {
        var plan = new TimetablePlan(new[]
        {
            new TimetableAssignment(new TimetableCell(월, 1), "국", AssignmentStatus.Pending, "L|국어|h|k"),
        });

        Assert.Equal(TimetableRunCheckpoint.FingerprintOf(plan.Assignments), plan.Fingerprint);
        Assert.Equal(16, plan.Fingerprint.Length);
    }
}
