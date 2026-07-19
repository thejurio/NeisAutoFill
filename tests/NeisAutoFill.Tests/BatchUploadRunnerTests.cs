using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F3 — 전과목 배치 오케스트레이션·재시도 (가짜 엔진, 실기기 불필요).</summary>
public class BatchUploadRunnerTests
{
    /// <summary>과목 전환·저장 동작을 지정할 수 있는 가짜 엔진.</summary>
    private sealed class FakeEngine : INeisEngine
    {
        public Func<string, (bool, string)> OnSelect = _ => (true, "");
        public Func<(bool, string)> OnSave = () => (true, "");

        public bool Connected => true;
        public void LaunchEdge() { }
        public Task<bool> AttachAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsAliveAsync() => Task.FromResult(true);
        public Task<NeisStatus> DetectStatusAsync(CancellationToken ct = default)
            => Task.FromResult(new NeisStatus(NeisScreenKind.EvaluationReady));
        public Task<bool> NavigateToAsync(NeisTarget target, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> GetCurrentSubjectAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> ReadSubjectOptionsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(System.Array.Empty<string>());
        public Task<(bool Ok, string Why)> SelectSubjectAsync(string subjectName, CancellationToken ct = default)
            => Task.FromResult(OnSelect(subjectName));
        public Task<(bool Ok, string Why)> SelectClassAsync(int grade, string @class,
            IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
            => Task.FromResult((true, ""));
        public Task<(bool Ok, string Why)> SelectNarrativeAxisAsync(int grade, string @class, string subject,
            IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
            => Task.FromResult((true, ""));
        public Task<(bool Ok, string Why)> QueryAsync(CancellationToken ct = default) => Task.FromResult((true, ""));
        public Task<(bool Ok, string Why)> SaveScreenAsync(CancellationToken ct = default)
            => Task.FromResult(OnSave());
        public Task<RunReport> RunSubjectAsync(SubjectSheet sheet, GradeScale scale, bool dryRun,
            IProgress<ProgressInfo> progress, Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null,
            CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> InspectDomAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<NarrativeReport> RunNarrativesAsync(string subjectName, IReadOnlyList<NarrativeEntry> entries,
            bool dryRun, int maxBytes, IProgress<ProgressInfo> progress, CancellationToken ct = default,
            Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null)
            => throw new NotImplementedException();
    }

    private static BatchUploadRunner.SubjectResult Ok(int done) => new(done, Array.Empty<SkipItem>(), 0, false);
    private static BatchUploadRunner.SubjectResult Fail(params SkipItem[] failed) => new(0, failed, 0, false);

    private static Task<List<BatchUploadRunner.SubjectOutcome>> Run(
        FakeEngine eng, IReadOnlyList<string> subjects, Func<string, BatchUploadRunner.SubjectResult> run)
    {
        var targets = subjects.Select(s => new BatchUploadRunner.SubjectTarget(s, s)).ToList();
        return BatchUploadRunner.RunAsync(targets, eng, s => Task.FromResult(run(s)), _ => { }, "건", CancellationToken.None);
    }

    [Fact]
    public async Task 모두_성공하면_전부_Success()
    {
        var outcomes = await Run(new FakeEngine(), new[] { "국어", "수학" }, _ => Ok(3));
        Assert.All(outcomes, o => Assert.Equal(BatchUploadRunner.SubjectStatus.Success, o.Status));
    }

    [Fact]
    public async Task switchSubjects_false면_과목전환_안하고_돈다()
    {
        // 전담 전체반: 대상이 반("3-1")이라 과목 콤보 전환을 끄면 SelectSubject 가 불리면 안 된다
        var eng = new FakeEngine { OnSelect = s => throw new Exception($"과목 전환 호출됨: {s}") };
        var targets = new[] { new BatchUploadRunner.SubjectTarget("3-1", "3-1"),
                              new BatchUploadRunner.SubjectTarget("3-2", "3-2") };
        var outcomes = await BatchUploadRunner.RunAsync(
            targets, eng, _ => Task.FromResult(Ok(3)), _ => { }, "건", CancellationToken.None,
            switchSubjects: false);
        Assert.All(outcomes, o => Assert.Equal(BatchUploadRunner.SubjectStatus.Success, o.Status));
    }

    [Fact]
    public async Task 한_과목_실패하면_그_뒤는_미도달()
    {
        var outcomes = await Run(new FakeEngine(), new[] { "국어", "수학", "영어" },
            s => s == "수학" ? Fail(new SkipItem("3", "김철수", "듣기", "매칭 실패")) : Ok(3));

        Assert.Equal(BatchUploadRunner.SubjectStatus.Success, outcomes[0].Status);
        Assert.Equal(BatchUploadRunner.SubjectStatus.Failed, outcomes[1].Status);
        Assert.Single(outcomes[1].FailedItems);
        Assert.Equal(BatchUploadRunner.SubjectStatus.NotReached, outcomes[2].Status);
    }

    [Fact]
    public async Task 과목전환_실패하면_전환실패_후_중단()
    {
        var eng = new FakeEngine { OnSelect = s => s == "수학" ? (false, "콤보 없음") : (true, "") };
        var outcomes = await Run(eng, new[] { "국어", "수학", "영어" }, _ => Ok(3));
        Assert.Equal(BatchUploadRunner.SubjectStatus.Success, outcomes[0].Status);
        Assert.Equal(BatchUploadRunner.SubjectStatus.SwitchFailed, outcomes[1].Status);
        Assert.Equal(BatchUploadRunner.SubjectStatus.NotReached, outcomes[2].Status);
    }

    [Fact]
    public async Task 저장_실패하면_저장실패()
    {
        var eng = new FakeEngine { OnSave = () => (false, "저장 버튼 없음") };
        var outcomes = await Run(eng, new[] { "국어" }, _ => Ok(3));
        Assert.Equal(BatchUploadRunner.SubjectStatus.SaveFailed, outcomes[0].Status);
    }

    [Fact]
    public async Task 입력값_없으면_생략하고_계속()
    {
        var outcomes = await Run(new FakeEngine(), new[] { "국어", "수학" }, s => s == "국어" ? Ok(0) : Ok(3));
        Assert.Equal(BatchUploadRunner.SubjectStatus.Skipped, outcomes[0].Status);
        Assert.Equal(BatchUploadRunner.SubjectStatus.Success, outcomes[1].Status);
    }

    [Fact]
    public async Task RetrySubjects_는_실패와_미도달만()
    {
        var outcomes = await Run(new FakeEngine(), new[] { "국어", "수학", "영어" },
            s => s == "수학" ? Fail(new SkipItem("1", "A", "가", "x")) : Ok(3));
        Assert.Equal(new[] { "수학", "영어" }, BatchUploadRunner.RetrySubjects(outcomes));   // 국어(성공) 제외
    }
}
