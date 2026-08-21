using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.App.Services;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Evaluation;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.ViewModels;

/// <summary>넣을지 말지 고를 수 있는 교과 한 줄.</summary>
public sealed class EvalSubjectRow(EvalSubjectPlan plan) : ObservableObject
{
    public EvalSubjectPlan Plan { get; } = plan;

    public string Subject => Plan.Subject;

    /// <summary>영역 3 · 성취기준 4 처럼 한눈에 보이는 요약.</summary>
    public string Summary => $"영역 {Plan.Areas.Count} · 성취기준 {Plan.StandardCount}";

    /// <summary>영역 이름들 — 무엇이 들어가는지 펼치지 않고도 보이게.</summary>
    public string Areas => string.Join(" · ", Plan.Areas.Select(a => a.Name));

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
    private bool _selected = true;

    /// <summary>넣고 난 결과. 아직이면 빈 문자열.</summary>
    public string Result
    {
        get => _result;
        set => SetProperty(ref _result, value);
    }
    private string _result = "";

    public bool Failed
    {
        get => _failed;
        set => SetProperty(ref _failed, value);
    }
    private bool _failed;
}

/// <summary>
/// 평가계획 탭 — 문서를 골라 읽고, 넣을 교과를 고르고, 나이스에 넣는다.
///
/// 화면에 보이는 순서가 곧 해야 하는 순서다:
/// <code>① 문서 고르기 → ② 교과 고르기 → ③ 입력 시작</code>
/// 시간표 탭과 같은 꼴로 만들어, 쓰는 사람이 두 번 배우지 않게 한다.
/// </summary>
public sealed class EvalPlanTabViewModel : ObservableObject
{
    private readonly EvalPlanSession _session;
    private readonly Action<string> _log;
    private readonly IProgress<ProgressInfo> _progress;
    private readonly Func<bool> _connected;
    private CancellationTokenSource? _cancel;

    public EvalPlanTabViewModel(
        EvalPlanSession session,
        Action<string> log,
        IProgress<ProgressInfo> progress,
        Func<bool> connected)
    {
        _session = session;
        _log = log;
        _progress = progress;
        _connected = connected;

        PickDocumentCommand = new AsyncRelayCommand(PickDocumentAsync);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
        StopCommand = new RelayCommand(Stop, () => IsRunning);
    }

    public AsyncRelayCommand PickDocumentCommand { get; }

    public AsyncRelayCommand RunCommand { get; }

    public RelayCommand StopCommand { get; }

    public ObservableCollection<EvalSubjectRow> Subjects { get; } = new();

    /// <summary>고른 문서 이름. 없으면 빈 문자열.</summary>
    public string DocumentName
    {
        get => _documentName;
        private set { SetProperty(ref _documentName, value); OnPropertyChanged(nameof(HasDocument)); }
    }
    private string _documentName = "";

    public bool HasDocument => DocumentName.Length > 0;

    /// <summary>문서를 읽은 요약 — 교과·영역·성취기준 수와 뺀 영역.</summary>
    public string DocumentSummary
    {
        get => _documentSummary;
        private set => SetProperty(ref _documentSummary, value);
    }
    private string _documentSummary = "평가계획 문서를 고르면 무엇이 들어가는지 보여 드립니다.";

    /// <summary>교과를 가릴 수 없어 뺀 영역 안내. 없으면 빈 문자열.</summary>
    public string SkippedNote
    {
        get => _skippedNote;
        private set { SetProperty(ref _skippedNote, value); OnPropertyChanged(nameof(HasSkipped)); }
    }
    private string _skippedNote = "";

    public bool HasSkipped => SkippedNote.Length > 0;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            SetProperty(ref _isRunning, value);
            OnPropertyChanged(nameof(CanRun));
            RunCommand.RaiseCanExecuteChanged();
            StopCommand.RaiseCanExecuteChanged();
        }
    }
    private bool _isRunning;

    /// <summary>지금 어느 단계까지 왔나 — 화면에서 다음에 할 일을 도드라지게 하는 데 쓴다.</summary>
    public int CurrentStep => !HasDocument ? 1 : Subjects.All(s => !s.Selected) ? 2 : 3;

    public bool CanRun => HasDocument && !IsRunning && _connected() && Subjects.Any(s => s.Selected);

    /// <summary>연결 상태가 바뀌면 실행 가능 여부도 바뀐다.</summary>
    public void RefreshRunnable()
    {
        OnPropertyChanged(nameof(CanRun));
        RunCommand.RaiseCanExecuteChanged();
    }

    // ── ① 문서 고르기 ─────────────────────────────────────

    private async Task PickDocumentAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "평가계획 문서 고르기",
            Filter = "평가계획 문서 (*.pdf;*.hwp;*.hwpx)|*.pdf;*.hwp;*.hwpx|모든 파일 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
        _log($"평가계획 문서를 읽고 있어요 — {Path.GetFileName(path)}");

        try
        {
            // 한컴 변환은 오래 걸릴 수 있어 화면을 붙들지 않는다
            var doc = await Task.Run(() => EvalPlanDocumentParser.Parse(
                PdfLayoutExtractor.ExtractAny(path, keepSpaces: true)));

            Subjects.Clear();
            foreach (var subject in doc.Subjects) Subjects.Add(new EvalSubjectRow(subject));

            DocumentName = Path.GetFileName(path);
            DocumentSummary = doc.Describe();

            SkippedNote = doc.Skipped.Count == 0
                ? ""
                : $"교과를 가릴 수 없어 뺀 영역 {doc.Skipped.Count} — {string.Join(" · ", doc.Skipped)}\n" +
                  "성취기준 코드가 없는 것들입니다 (창의적 체험활동·학교자율시간).";

            _log($"문서를 읽었습니다 — {doc.Describe()}");
            OnPropertyChanged(nameof(CurrentStep));
            RefreshRunnable();
        }
        catch (Exception ex)
        {
            DocumentName = "";
            DocumentSummary = $"문서를 읽지 못했습니다 — {ex.Message}";
            _log(DocumentSummary);
        }
    }

    // ── ③ 입력 ────────────────────────────────────────────

    private void Stop()
    {
        _cancel?.Cancel();
        _log("중지했습니다.");
    }

    private async Task RunAsync()
    {
        var chosen = Subjects.Where(s => s.Selected).ToList();
        if (chosen.Count == 0) return;

        _cancel = new CancellationTokenSource();
        IsRunning = true;

        try
        {
            var blocked = await _session.PreflightAsync(_cancel.Token);
            if (blocked is not null) { _log(blocked); return; }

            _log($"{_session.Scope} 화면에 넣습니다 — 교과 {chosen.Count}개");

            foreach (var row in chosen)
            {
                if (_cancel.IsCancellationRequested) break;

                row.Result = "넣는 중…";
                row.Failed = false;

                var result = await _session.RunSubjectAsync(row.Plan, _progress, _cancel.Token);

                row.Result = result.Describe();
                row.Failed = !result.Ok;
                _log(result.Describe());

                // 한 교과가 막히면 다음으로 넘어가지 않는다 — 무엇이 잘못됐는지 먼저 봐야 한다
                if (!result.Ok) break;
            }
        }
        finally
        {
            IsRunning = false;
            _cancel?.Dispose();
            _cancel = null;
            _progress.Report(new(""));
        }
    }
}
