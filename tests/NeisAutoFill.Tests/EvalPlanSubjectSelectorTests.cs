using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Tests;

public class EvalPlanSubjectSelectorTests
{
    private sealed class FakeEngine : INeisEngine
    {
        public Func<string, (bool Ok, string Why)> OnSelect { get; set; } = _ => (true, "");
        public string? CurrentSubject { get; set; }
        public List<string> SelectedSubjects { get; } = new();
        public int CurrentSubjectReads { get; private set; }

        public bool Connected => true;
        public TimetableTools? Timetable => null;
        public void LaunchEdge() { }
        public Task<bool> AttachAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsAliveAsync() => Task.FromResult(true);
        public Task<NeisStatus> DetectStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(new NeisStatus(NeisScreenKind.EvaluationReady));
        public Task<bool> HasSessionWarningAsync() => Task.FromResult(false);
        public Task<bool> NavigateToAsync(NeisTarget target, IProgress<ProgressInfo>? progress = null,
            CancellationToken ct = default) => Task.FromResult(true);

        public Task<string?> GetCurrentSubjectAsync(CancellationToken ct = default)
        {
            CurrentSubjectReads++;
            return Task.FromResult(CurrentSubject);
        }

        public Task<IReadOnlyList<string>> ReadSubjectOptionsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<(bool Ok, string Why)> SelectSubjectAsync(string subjectName,
            CancellationToken ct = default)
        {
            SelectedSubjects.Add(subjectName);
            return Task.FromResult(OnSelect(subjectName));
        }

        public Task<(bool Ok, string Why)> SelectClassAsync(int grade, string @class,
            IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            Task.FromResult((true, ""));
        public Task<(bool Ok, string Why)> SelectNarrativeAxisAsync(int grade, string @class, string subject,
            IProgress<ProgressInfo>? progress = null, CancellationToken ct = default) =>
            Task.FromResult((true, ""));
        public Task<(bool Ok, string Why)> QueryAsync(CancellationToken ct = default) =>
            Task.FromResult((true, ""));
        public Task<(bool Ok, string Why)> SaveScreenAsync(CancellationToken ct = default) =>
            Task.FromResult((true, ""));
        public Task<RunReport> RunSubjectAsync(SubjectSheet sheet, GradeScale scale, bool dryRun,
            IProgress<ProgressInfo> progress,
            Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> InspectDomAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<NarrativeReport> RunNarrativesAsync(string subjectName,
            IReadOnlyList<NarrativeEntry> entries, bool dryRun, int maxBytes,
            IProgress<ProgressInfo> progress, CancellationToken ct = default,
            Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null,
            bool byOrder = false) => throw new NotSupportedException();
    }

    [Fact]
    public async Task 교과를_선택하고_화면값까지_같으면_통과한다()
    {
        var engine = new FakeEngine { CurrentSubject = "국어" };

        var result = await EvalPlanSubjectSelector.SelectAndVerifyAsync(engine, "국어");

        Assert.True(result.Ok);
        Assert.Equal("국어", result.CurrentSubject);
        Assert.Equal(new[] { "국어" }, engine.SelectedSubjects);
        Assert.Equal(1, engine.CurrentSubjectReads);
    }

    [Fact]
    public async Task 나이스_목록에_교과가_없으면_화면을_읽지_않고_멈춘다()
    {
        var engine = new FakeEngine
        {
            OnSelect = _ => (false, INeisEngine.SubjectNotInList),
            CurrentSubject = "국어",
        };

        var result = await EvalPlanSubjectSelector.SelectAndVerifyAsync(engine, "학교자율시간");

        Assert.False(result.Ok);
        Assert.Contains("나이스 교과 목록", result.Message);
        Assert.Equal(0, engine.CurrentSubjectReads);
    }

    [Fact]
    public async Task 선택_호출이_성공해도_화면_교과가_다르면_입력을_차단한다()
    {
        var engine = new FakeEngine { CurrentSubject = "국어" };

        var result = await EvalPlanSubjectSelector.SelectAndVerifyAsync(engine, "수학");

        Assert.False(result.Ok);
        Assert.Contains("화면은 '국어'", result.Message);
        Assert.Contains("입력하지 않고 멈췄습니다", result.Message);
    }

    [Fact]
    public async Task 빈_교과명은_나이스를_조작하지_않는다()
    {
        var engine = new FakeEngine();

        var result = await EvalPlanSubjectSelector.SelectAndVerifyAsync(engine, "   ");

        Assert.False(result.Ok);
        Assert.Empty(engine.SelectedSubjects);
        Assert.Equal(0, engine.CurrentSubjectReads);
    }
}
