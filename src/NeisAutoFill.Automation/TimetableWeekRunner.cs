using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Automation;

/// <summary>셀 한 건의 처리 기록.</summary>
public sealed record CellRunRecord(TimetableCell Cell, CellWriteOutcome Outcome, string Detail);

/// <summary>주 한 개를 처리한 결과.</summary>
/// <param name="WeekStart">주 시작일(월요일)</param>
/// <param name="Records">처리한 셀들 — 중단됐으면 거기까지만</param>
/// <param name="StoppedReason">중단 사유. null 이면 끝까지 진행</param>
public sealed record WeekRunResult(
    DateOnly WeekStart,
    IReadOnlyList<CellRunRecord> Records,
    string? StoppedReason)
{
    public bool Completed => StoppedReason is null;

    /// <summary>실제로 값이 바뀐 셀 — 아직 저장하지 않았다.</summary>
    public IReadOnlyList<CellRunRecord> Changed =>
        Records.Where(r => r.Outcome == CellWriteOutcome.Written).ToList();

    public IReadOnlyList<CellRunRecord> Skipped =>
        Records.Where(r => r.Outcome is CellWriteOutcome.AlreadyMatches or CellWriteOutcome.CellUnavailable).ToList();

    /// <summary>사용자에게 보여 줄 한 줄 요약.</summary>
    public string Describe() =>
        $"{WeekStart:MM-dd} 주 — 입력 {Changed.Count}칸 · 건너뜀 {Skipped.Count}칸" +
        (Completed ? "" : $" · 중단: {StoppedReason}");
}

/// <summary>
/// 실행 계획을 <b>주 단위로</b> 입력한다 (기술설계 §12, 로드맵 T7).
///
/// 안전 규칙:
/// <list type="bullet">
/// <item><b>저장하지 않는다.</b> 저장은 별도 승인 아래 T8 에서 한다</item>
/// <item>한 건이라도 실패하면 <b>그 자리에서 멈춘다</b> — 다음 셀로 넘어가지 않는다</item>
/// <item>이미 같은 값·수업 없는 날은 실패가 아니라 건너뜀이다</item>
/// <item>취소하면 즉시 멈추고, 그때까지 바뀐 셀을 그대로 보고한다</item>
/// </list>
/// </summary>
public sealed class TimetableWeekRunner(TimetableReader reader, TimetableCellWriter writer)
{
    /// <summary>
    /// 한 주를 처리한다. 주차 이동은 이 메서드가 한다.
    /// </summary>
    /// <param name="weekStart">주 시작일(월요일)</param>
    /// <param name="assignments">그 주의 계획 — 입력 예정만 넘겨도 되고 전부 넘겨도 된다</param>
    public async Task<WeekRunResult> RunWeekAsync(
        DateOnly weekStart,
        IReadOnlyList<TimetableAssignment> assignments,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var records = new List<CellRunRecord>();

        var targets = assignments
            .Where(a => a.WillWrite)
            .OrderBy(a => a.Cell.Date).ThenBy(a => a.Cell.Period)
            .ToList();

        if (targets.Count == 0)
            return new(weekStart, records, null);

        // 그 주로 이동 — 화면에 없는 주의 셀은 쓸 수 없다
        var week = await reader.SelectWeekForDateAsync(targets[0].Cell.Date);
        if (week is null)
            return new(weekStart, records, $"{targets[0].Cell.Date:yyyy-MM-dd} 이 든 주차를 찾지 못했습니다.");

        var done = 0;
        foreach (var a in targets)
        {
            if (ct.IsCancellationRequested)
                return new(weekStart, records, "사용자가 취소했습니다.");

            progress?.Report(new($"{a.Cell} 입력 중…", ++done, targets.Count));

            var r = await writer.WriteAsync(a.Cell, a.TargetStableKey, allowOverwrite: false, ct);
            records.Add(new(a.Cell, r.Outcome, r.Detail));

            if (IsStopping(r.Outcome))
                return new(weekStart, records, $"{a.Cell} — {r.Detail}");
        }

        return new(weekStart, records, null);
    }

    /// <summary>
    /// 여러 주를 이어서 처리한다. <b>한 주가 막히면 다음 주로 넘어가지 않는다</b>(기술설계 §12).
    /// </summary>
    public async Task<IReadOnlyList<WeekRunResult>> RunAsync(
        TimetablePlan plan, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var results = new List<WeekRunResult>();

        foreach (var week in plan.ByWeek)
        {
            var r = await RunWeekAsync(week.Key, week.ToList(), progress, ct);
            results.Add(r);

            if (!r.Completed) break;   // 실패한 주에서 멈춘다
        }

        return results;
    }

    /// <summary>이 결과를 만나면 멈춘다. 건너뜀은 정상 진행이다.</summary>
    private static bool IsStopping(CellWriteOutcome outcome) => outcome switch
    {
        CellWriteOutcome.Written => false,
        CellWriteOutcome.AlreadyMatches => false,
        CellWriteOutcome.CellUnavailable => false,   // 휴업일 — 넘어가도 된다
        _ => true,
    };
}
