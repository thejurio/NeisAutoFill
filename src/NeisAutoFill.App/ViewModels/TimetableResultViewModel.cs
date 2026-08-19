using NeisAutoFill.Automation;

namespace NeisAutoFill.App.ViewModels;

/// <summary>결과 표의 한 줄 = 주 하나.</summary>
public sealed class WeekResultRow
{
    public WeekResultRow(WeekOutcome outcome)
    {
        Week = $"{outcome.WeekStart:MM-dd} 주";
        Status = outcome.Phase switch
        {
            WeekPhase.Completed => "완료",
            WeekPhase.NothingToDo => "할 일 없음",
            _ => "중단",
        };
        Written = outcome.Written == 0 ? "" : $"{outcome.Written}칸";
        Skipped = outcome.Skipped == 0 ? "" : $"{outcome.Skipped}칸";
        Detail = outcome.Detail;
        IsStopped = outcome.Phase == WeekPhase.Stopped;
        IsDone = outcome.Phase == WeekPhase.Completed;
    }

    public string Week { get; }
    public string Status { get; }
    public string Written { get; }
    public string Skipped { get; }
    public string Detail { get; }
    public bool IsStopped { get; }
    public bool IsDone { get; }
}

/// <summary>
/// 연간 입력 결과 대시보드 (로드맵 T8).
///
/// 성공만 보여 주면 쓸모가 없다 — <b>어디서 왜 멈췄는지</b>와
/// <b>다시 시도하면 무엇이 남는지</b>를 같이 보여 준다.
/// </summary>
public sealed class TimetableResultViewModel
{
    public TimetableResultViewModel(BatchRunResult result)
    {
        Rows = result.Weeks.Select(w => new WeekResultRow(w)).ToList();

        var doneWeeks = result.Weeks.Count(w => w.Phase == WeekPhase.Completed);
        var remaining = result.Weeks.Count(w => w.Phase == WeekPhase.Stopped);

        Headline = result.Completed
            ? $"끝났습니다 — {doneWeeks}개 주 · {result.TotalWritten}칸 입력·저장"
            : $"{doneWeeks}개 주까지 끝내고 멈췄습니다 — {result.TotalWritten}칸 입력·저장";

        Detail = result.StoppedReason ?? "계획한 주를 모두 처리했습니다.";

        // 멈춘 지점부터 다시 하면 된다. 끝난 주는 체크포인트가 막아 주므로 두 번 입력되지 않는다.
        CanRetry = !result.Completed;
        RetryHint = CanRetry
            ? "문제를 해결한 뒤 [이어서 다시 시도]를 누르면 멈춘 주부터 이어 갑니다. 끝난 주는 다시 건드리지 않습니다."
            : "";

        Checkpoint = result.Checkpoint.Describe();
    }

    public IReadOnlyList<WeekResultRow> Rows { get; }
    public string Headline { get; }
    public string Detail { get; }
    public string Checkpoint { get; }
    public bool CanRetry { get; }
    public string RetryHint { get; }
    public bool HasRetryHint => RetryHint.Length > 0;
}
