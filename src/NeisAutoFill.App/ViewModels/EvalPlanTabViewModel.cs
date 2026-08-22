using System.Collections.ObjectModel;
using System.Windows;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.App.Services;
using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Evaluation;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.App.ViewModels;

/// <summary>영역 하나를 펼쳐 보여 주는 줄 (상세 창에서 쓴다).</summary>
public sealed record EvalAreaDetail(string Area, string Standard, IReadOnlyList<EvalCriterion> Criteria);

/// <summary>넣을지 말지 고를 수 있는 교과 한 칸.</summary>
public sealed class EvalSubjectRow(EvalSubjectPlan plan, Action changed) : ObservableObject
{
    public EvalSubjectPlan Plan { get; } = plan;

    public string Subject => Plan.Subject;

    /// <summary>영역 3 · 성취기준 4 처럼 한눈에 보이는 요약.</summary>
    public string Summary => $"영역 {Plan.Areas.Count} · 성취기준 {Plan.StandardCount}";

    /// <summary>영역 이름들 — 무엇이 들어가는지 펼치지 않고도 보이게.</summary>
    public string Areas => string.Join(" · ", Plan.Areas.Select(a => a.Name));

    /// <summary>상세 창에 보여 줄 내용 — 영역마다 성취기준과 단계별 평가기준.</summary>
    public IReadOnlyList<EvalAreaDetail> Details => Plan.Areas
        .SelectMany(a => a.Standards.Select(s => new EvalAreaDetail(a.Name, s.Standard, s.Criteria)))
        .ToList();

    public bool Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) changed(); }
    }
    private bool _selected = true;

    /// <summary>넣고 난 결과. 아직이면 빈 문자열.</summary>
    public string Result
    {
        get => _result;
        set { SetProperty(ref _result, value); OnPropertyChanged(nameof(HasResult)); }
    }
    private string _result = "";

    public bool HasResult => Result.Length > 0;

    public bool Failed
    {
        get => _failed;
        set => SetProperty(ref _failed, value);
    }
    private bool _failed;
}

/// <summary>
/// 평가계획을 나이스에 넣는 창의 속.
///
/// <b>자료는 [자료 준비]에 있는 것을 그대로 쓴다</b>(사용자 요청 2026-08-22) —
/// 사용자가 프로그램 안에서 계획을 고칠 수 있고, 고친 그대로 나이스에 들어가야 하기 때문이다.
/// 그래서 이 창에는 문서를 고르는 자리가 없다.
/// </summary>
public sealed class EvalPlanTabViewModel : ObservableObject
{
    private readonly EvalPlanSession _session;
    private readonly Func<IReadOnlyList<SubjectPlan>> _plans;
    private readonly Func<IReadOnlyList<string>> _levels;
    private readonly Action<string> _log;
    private readonly IProgress<ProgressInfo> _progress;
    private readonly Func<bool> _connected;
    private CancellationTokenSource? _cancel;

    public EvalPlanTabViewModel(
        EvalPlanSession session,
        Func<IReadOnlyList<SubjectPlan>> plans,
        Func<IReadOnlyList<string>> levels,
        Action<string> log,
        IProgress<ProgressInfo> progress,
        Func<bool> connected)
    {
        _session = session;
        _plans = plans;
        _levels = levels;
        _log = log;
        _progress = progress;
        _connected = connected;

        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
        SelectAllCommand = new RelayCommand<string>(SelectAll);
    }

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand StopCommand { get; }

    /// <summary>"모두" / "해제" — 교과가 열 개가 넘어 하나씩 누르기 번거롭다.</summary>
    public RelayCommand<string> SelectAllCommand { get; }

    public ObservableCollection<EvalSubjectRow> Subjects { get; } = new();

    /// <summary>창을 열 때 부른다 — [자료 준비]에 지금 들어 있는 계획을 읽어 온다.</summary>
    public void Refresh()
    {
        var doc = EvalPlanFromWorkspace.Convert(_plans(), _levels());

        Subjects.Clear();
        foreach (var subject in doc.Subjects) Subjects.Add(new EvalSubjectRow(subject, RefreshRunnable));

        // 처음에는 전부 고른 상태로 — 체크를 <b>세터를 거쳐</b> 켜야 화면에도 반영된다
        SelectAll("all");

        Summary = Subjects.Count == 0
            ? "[자료 준비]에 평가계획이 없습니다."
            : $"교과 {doc.Subjects.Count} · 영역 {doc.AreaCount} · 성취기준 {doc.StandardCount}";

        OnPropertyChanged(nameof(HasPlans));
        RefreshRunnable();
    }

    public bool HasPlans => Subjects.Count > 0;

    /// <summary>무엇이 들어가는지 한 줄 요약.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
    private string _summary = "";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            SetProperty(ref _isRunning, value);
            RefreshRunnable();
            StopCommand.RaiseCanExecuteChanged();
        }
    }
    private bool _isRunning;

    public bool CanRun => Blocker is null;

    /// <summary>
    /// 지금 [입력 시작]을 누를 수 없는 이유. 누를 수 있으면 null.
    ///
    /// <b>꺼진 단추만 두지 않는다.</b> 왜 안 되는지 모르면 사용자는 손쓸 방법이 없다 —
    /// 실제로 브라우저가 안 떠 있어 잠겼는데 화면에 아무 말이 없었다(2026-08-21).
    /// </summary>
    public string? Blocker =>
        !HasPlans ? "[자료 준비]에서 평가계획을 먼저 넣어 주세요."
        : !_connected() ? "나이스에 연결되어 있지 않습니다 — [🌐 NEIS 접속] 으로 브라우저를 열고 로그인해 주세요."
        : !Subjects.Any(s => s.Selected) ? "넣을 교과를 하나 이상 골라 주세요."
        : IsRunning ? "넣는 중입니다."
        : null;

    public bool HasBlocker => Blocker is not null;

    public void RefreshRunnable()
    {
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(Blocker));
        OnPropertyChanged(nameof(HasBlocker));
        RunCommand.RaiseCanExecuteChanged();
    }

    private void SelectAll(string? mode)
    {
        var on = mode != "none";
        foreach (var row in Subjects) row.Selected = on;
    }

    private void Stop()
    {
        _cancel?.Cancel();
        _log("중지했습니다.");
    }

    /// <summary>한 단계에서 고른 교과를 차례로 돈다. 끝까지 갔으면 true.</summary>
    /// <summary>어디서 왜 멈췄는지. null 이면 끝까지 갔다.</summary>
    private string? _stopped;

    private async Task<bool> RunStepAsync(
        EvalStep step, string label, IReadOnlyList<EvalSubjectRow> chosen,
        Func<EvalSubjectPlan, CancellationToken, Task<EvalSubjectResult>> run)
    {
        var blocked = await _session.EnterAsync(step, _cancel!.Token);
        if (blocked is not null)
        {
            _stopped = $"{label} 화면으로 옮기지 못했습니다 — {blocked}";
            _log(_stopped);
            return false;
        }

        _log($"── {label} 단계 — 교과 {chosen.Count}개");

        foreach (var row in chosen)
        {
            if (_cancel.IsCancellationRequested) return false;

            row.Result = $"{label} 넣는 중…";
            row.Failed = false;

            var result = await run(row.Plan, _cancel.Token);

            row.Result = result.Describe();
            row.Failed = !result.Ok;
            _log(result.Describe());

            // 한 교과가 막히면 다음으로 넘어가지 않는다 — 무엇이 잘못됐는지 먼저 봐야 한다
            if (!result.Ok)
            {
                _stopped = $"[{label}] {result.Subject} 에서 멈췄습니다.\n\n{result.Failure}";
                return false;
            }
        }

        return true;
    }

    private async Task RunAsync()
    {
        var chosen = Subjects.Where(s => s.Selected).ToList();
        if (chosen.Count == 0) return;

        _cancel = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            _stopped = null;

            var blocked = await _session.PreflightAsync(_cancel.Token);
            if (blocked is not null) { _stopped = blocked; _log(blocked); return; }

            var scope = _session.Scope!;
            _log($"{scope.SchoolYear}학년도 {scope.Semester}학기 {scope.Grade}학년에서 " +
                 $"교과 {chosen.Count}개를 자동으로 바꿔 가며 넣습니다.");

            // <b>단계 하나에서 전 교과를 돌고, 그다음 단계로 넘어간다</b>(사용자 결정 2026-08-22).
            //
            // 교과마다 두 단계를 오가면 단계 전환이 교과 수의 두 배만큼 일어나고, 전환마다
            // 조회와 대기가 붙어 느리다. 게다가 <b>단계를 바꾸면 교과가 첫 항목으로 되돌아가</b>
            // 엉뚱한 교과 화면에서 찾다 멈추는 사고가 실제로 났다.
            if (!await RunStepAsync(EvalStep.Standards, "성취기준", chosen,
                    (p, ct) => _session.RunStandardsAsync(p, _progress, ct))) return;

            if (!await RunStepAsync(EvalStep.Criteria, "평가기준", chosen,
                    (p, ct) => _session.RunCriteriaAsync(p, _progress, ct))) return;
        }
        finally
        {
            IsRunning = false;
            var canceled = _cancel?.IsCancellationRequested ?? false;
            _cancel?.Dispose();
            _cancel = null;
            _progress.Report(new(""));

            if (!canceled) Report();
        }
    }

    /// <summary>
    /// 끝나고 <b>한 줄로</b> 알린다.
    ///
    /// 몇 건을 넣었는지는 사용자가 알 필요 없다(지적 2026-08-22) — 끝났는지, 막혔으면 왜인지만 알면 된다.
    /// 자세한 것은 로그에 남아 있다.
    /// </summary>
    private void Report()
    {
        // <b>주인 창을 반드시 넘긴다.</b> 안 넘기면 상자가 메인 창 뒤에 떠서,
        // 사용자에게는 "단추가 이유 없이 꺼져 있다"로만 보인다.
        var owner = Application.Current?.MainWindow;

        if (_stopped is null)
        {
            Show(owner, "작업을 완료했습니다.", "평가계획 입력", MessageBoxImage.Information);
            return;
        }

        Show(owner, _stopped, "평가계획 입력 중단", MessageBoxImage.Warning);
    }

    /// <summary>주인 창 위에 띄운다 — 주인이 없으면 그냥 띄운다(디자이너·시험 상황).</summary>
    private static void Show(Window? owner, string text, string title, MessageBoxImage icon)
    {
        if (owner is null) MessageBox.Show(text, title, MessageBoxButton.OK, icon);
        else MessageBox.Show(owner, text, title, MessageBoxButton.OK, icon);
    }
}
