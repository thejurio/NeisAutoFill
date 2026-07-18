using System.Collections.ObjectModel;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Automation;

namespace NeisAutoFill.App.ViewModels;

using Outcome = Automation.BatchUploadRunner.SubjectOutcome;
using Status = Automation.BatchUploadRunner.SubjectStatus;

/// <summary>전과목 입력 결과 대시보드 — 과목별 성공/실패 + 실패 학생 상세 + 실패·미도달 재시도.</summary>
public sealed class BatchResultViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>, Task<IReadOnlyList<Outcome>>> _retry;
    private readonly List<Outcome> _all;

    public BatchResultViewModel(
        IReadOnlyList<Outcome> outcomes, string unit,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<Outcome>>> retry)
    {
        _ = unit;
        _retry = retry;
        _all = outcomes.ToList();
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => CanRetry && !IsBusy);
        Refresh();
    }

    public ObservableCollection<BatchSubjectRow> Rows { get; } = new();

    private string _summary = "";
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public bool CanRetry { get; private set; }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(RetryLabel));
                (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    public string RetryLabel => IsBusy ? "재시도 중..." : "↻ 실패·미도달 재시도";

    public ICommand RetryCommand { get; }

    private async Task RetryAsync()
    {
        var subs = BatchUploadRunner.RetrySubjects(_all);
        if (subs.Count == 0) return;
        IsBusy = true;
        try
        {
            var updated = await _retry(subs);
            var byName = updated.ToDictionary(o => o.Subject);
            for (int i = 0; i < _all.Count; i++)
                if (byName.TryGetValue(_all[i].Subject, out var u)) _all[i] = u;
        }
        finally { IsBusy = false; }
        Refresh();
    }

    private void Refresh()
    {
        Rows.Clear();
        foreach (var o in _all) Rows.Add(BatchSubjectRow.From(o));

        int success = _all.Count(o => o.Status == Status.Success);
        int skipped = _all.Count(o => o.Status == Status.Skipped);
        int failed = _all.Count(o => o.Status is Status.Failed or Status.SwitchFailed or Status.SaveFailed);
        int notReached = _all.Count(o => o.Status == Status.NotReached);
        int cancelled = _all.Count(o => o.Status == Status.Cancelled);

        var parts = new List<string> { $"성공 {success}" };
        if (failed > 0) parts.Add($"실패 {failed}");
        if (cancelled > 0) parts.Add($"취소 {cancelled}");
        if (notReached > 0) parts.Add($"미도달 {notReached}");
        if (skipped > 0) parts.Add($"생략 {skipped}");
        Summary = $"{_all.Count}과목 중 " + string.Join(" · ", parts);

        CanRetry = _all.Any(o => o.NeedsRetry);
        OnPropertyChanged(nameof(CanRetry));
        (RetryCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

/// <summary>대시보드 한 과목 행 (표시용).</summary>
public sealed class BatchSubjectRow
{
    public required string Subject { get; init; }
    public required string Badge { get; init; }
    public required System.Windows.Media.Brush BadgeBg { get; init; }
    public required System.Windows.Media.Brush BadgeFg { get; init; }
    public required string Detail { get; init; }
    public IReadOnlyList<string> FailedItems { get; init; } = Array.Empty<string>();
    public bool HasFailedItems => FailedItems.Count > 0;

    public static BatchSubjectRow From(Outcome o)
    {
        var (badge, bg, fg) = o.Status switch
        {
            Status.Success => ("성공", "#E5F5EE", "#1A7A55"),
            Status.Skipped => ("생략", "#EEF1F7", "#5A6478"),
            Status.NotReached => ("미도달", "#F1F5F9", "#94A3B8"),
            Status.Cancelled => ("취소", "#FDF3E2", "#B7791F"),
            Status.SwitchFailed => ("전환 실패", "#FDECEA", "#C0392B"),
            Status.SaveFailed => ("저장 실패", "#FDECEA", "#C0392B"),
            _ => ("실패", "#FDECEA", "#C0392B"),
        };
        var failed = o.FailedItems
            .Select(f => $"{f.No} {f.Name} · {f.Area} · {f.Reason}").ToList();
        return new BatchSubjectRow
        {
            Subject = o.Subject, Badge = badge, BadgeBg = Brush(bg), BadgeFg = Brush(fg),
            Detail = o.Message, FailedItems = failed,
        };
    }

    private static System.Windows.Media.Brush Brush(string hex)
    {
        var b = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
