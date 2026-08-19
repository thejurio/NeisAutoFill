using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Automation;

/// <summary>주 하나가 어떻게 끝났는지.</summary>
public enum WeekPhase
{
    /// <summary>입력·저장·재조회 검증까지 통과했다. 이때만 체크포인트가 생긴다.</summary>
    Completed,
    /// <summary>할 일이 없었다 (이미 다 맞거나, 앞선 실행에서 끝냈다).</summary>
    NothingToDo,
    /// <summary>도중에 멈췄다. 다음 주로 넘어가지 않는다.</summary>
    Stopped,
}

/// <param name="Written">실제로 값을 넣은 칸</param>
/// <param name="Skipped">이미 같거나 수업 없는 날이라 넘어간 칸</param>
/// <param name="Detail">사용자에게 그대로 보여 줄 설명</param>
public sealed record WeekOutcome(
    DateOnly WeekStart,
    WeekPhase Phase,
    int Written,
    int Skipped,
    string Detail,
    IReadOnlyList<CellRunRecord> Records)
{
    public string Describe() => $"{WeekStart:MM-dd} 주 — " + Phase switch
    {
        WeekPhase.Completed => $"완료 (입력 {Written}칸 · 건너뜀 {Skipped}칸)",
        WeekPhase.NothingToDo => $"할 일 없음 ({Detail})",
        _ => $"중단 — {Detail}",
    };
}

/// <param name="Checkpoint">마지막 체크포인트 — 중단됐어도 여기까지는 확실히 끝났다</param>
/// <param name="StoppedReason">중단 사유. null 이면 계획한 주를 모두 끝냈다</param>
public sealed record BatchRunResult(
    IReadOnlyList<WeekOutcome> Weeks,
    TimetableRunCheckpoint Checkpoint,
    string? StoppedReason)
{
    public bool Completed => StoppedReason is null;

    public int TotalWritten => Weeks.Sum(w => w.Written);

    public IReadOnlyList<WeekOutcome> Failed => Weeks.Where(w => w.Phase == WeekPhase.Stopped).ToList();
}

/// <summary>실행에 필요한 재료. 주마다 계획을 <b>다시</b> 만들기 때문에 원본을 통째로 들고 있어야 한다.</summary>
/// <param name="Lessons">원본 문서에서 읽은 수업 전체</param>
/// <param name="Rules">확정된 매핑 규칙</param>
/// <param name="Catalog">나이스 과목·교사 목록</param>
/// <param name="Weeks">처리할 주의 시작일(월요일), 순서대로</param>
/// <param name="AllowOverwrite">이미 값이 있는 칸을 덮어쓸지 — 사용자가 동의 창에서 허용했을 때만 true</param>
public sealed record TimetableRunRequest(
    IReadOnlyList<TimetableSourceLesson> Lessons,
    IReadOnlyList<TimetableMappingRule> Rules,
    TimetableCatalog Catalog,
    IReadOnlyList<DateOnly> Weeks,
    DateOnly? TermStart = null,
    DateOnly? TermEnd = null,
    bool AllowOverwrite = false);

/// <summary>
/// 연간 입력을 <b>주 단위로</b> 끝까지 진행한다 (기술설계 §12, 로드맵 T8).
///
/// 한 주의 절차는 언제나 이 순서다:
/// <code>
/// 주차 이동 → 지금 값 다시 읽기 → 계획 다시 만들기 → 입력 → 저장 → 재조회 검증 → 체크포인트
/// </code>
///
/// 지켜야 할 것:
/// <list type="bullet">
/// <item><b>매 주 계획을 다시 만든다.</b> 과거 기록이 아니라 나이스의 현재 값이 최종 권위다 —
///       사람이 그 사이에 직접 고쳤을 수 있다</item>
/// <item><b>저장하고 다시 조회해서 값이 맞을 때만</b> 완료로 기록한다</item>
/// <item>한 주라도 막히면 <b>거기서 멈춘다.</b> 다음 주로 자동 진행하지 않는다</item>
/// <item>이미 완료된 주는 건드리지 않는다</item>
/// </list>
/// </summary>
public sealed class TimetableBatchRunner(
    TimetableReader reader,
    TimetableCellWriter writer,
    TimetableSaver saver)
{
    /// <param name="checkpoint">이어서 할 기록. 처음이면 <see cref="TimetableRunCheckpoint.Start"/></param>
    /// <param name="onCheckpoint">한 주가 끝날 때마다 불린다 — 여기서 즉시 파일에 남긴다</param>
    public async Task<BatchRunResult> RunAsync(
        TimetableRunRequest request,
        TimetableRunCheckpoint checkpoint,
        Action<TimetableRunCheckpoint>? onCheckpoint = null,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var outcomes = new List<WeekOutcome>();
        var done = 0;

        foreach (var weekStart in request.Weeks)
        {
            if (ct.IsCancellationRequested)
                return new(outcomes, checkpoint, "사용자가 취소했습니다.");

            done++;

            if (checkpoint.IsCompleted(weekStart))
            {
                outcomes.Add(new(weekStart, WeekPhase.NothingToDo, 0, 0,
                    "앞선 실행에서 이미 끝냈습니다.", Array.Empty<CellRunRecord>()));
                continue;
            }

            progress?.Report(new($"{weekStart:MM-dd} 주를 처리하고 있어요…", done, request.Weeks.Count));

            var outcome = await RunWeekAsync(weekStart, request, progress, ct);
            outcomes.Add(outcome);

            if (outcome.Phase == WeekPhase.Stopped)
                return new(outcomes, checkpoint, $"{weekStart:MM-dd} 주 — {outcome.Detail}");

            checkpoint = checkpoint.WithWeekDone(weekStart, DateTimeOffset.Now);
            onCheckpoint?.Invoke(checkpoint);
        }

        return new(outcomes, checkpoint, null);
    }

    private async Task<WeekOutcome> RunWeekAsync(
        DateOnly weekStart, TimetableRunRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var none = Array.Empty<CellRunRecord>();

        WeekOutcome Stop(string detail, IReadOnlyList<CellRunRecord>? records = null) =>
            new(weekStart, WeekPhase.Stopped, 0, 0, detail, records ?? none);

        var lessons = request.Lessons.Where(l => l.Cell.WeekStart == weekStart).ToList();
        if (lessons.Count == 0)
            return new(weekStart, WeekPhase.NothingToDo, 0, 0, "이 주에는 원본 수업이 없습니다.", none);

        // ① 그 주로 이동해서 지금 값을 다시 읽는다
        var week = await reader.SelectWeekForDateAsync(lessons[0].Cell.Date);
        if (week is null) return Stop($"{lessons[0].Cell.Date:yyyy-MM-dd} 이 든 주차를 찾지 못했습니다.");

        var plan = await PlanFromScreenAsync(lessons, request);

        // ② 다시 만든 계획에 막힌 게 있다 — 그 사이에 나이스 쪽이 바뀐 것이다
        if (plan.Blocking.Count > 0)
        {
            var first = plan.Blocking[0];
            return Stop($"{first.Cell} — {first.Reason} (막힌 칸 {plan.Blocking.Count}개)");
        }

        var targets = plan.Writable;
        var skipped = plan.Assignments.Count(a => a.Status
            is AssignmentStatus.AlreadyMatches or AssignmentStatus.CellUnavailable or AssignmentStatus.Skipped);

        if (targets.Count == 0)
            return new(weekStart, WeekPhase.NothingToDo, 0, skipped,
                $"넣을 칸이 없습니다 (이미 맞음·건너뜀 {skipped}칸).", none);

        // ③ 입력 — 한 칸이라도 실패하면 저장하지 않는다
        var records = new List<CellRunRecord>();
        var cellDone = 0;

        foreach (var a in targets.OrderBy(a => a.Cell.Date).ThenBy(a => a.Cell.Period))
        {
            if (ct.IsCancellationRequested) return Stop("사용자가 취소했습니다.", records);

            progress?.Report(new($"{a.Cell} 입력 중…", ++cellDone, targets.Count));

            var r = await writer.WriteAsync(a.Cell, a.TargetStableKey, a.Overwrite, ct);
            records.Add(new(a.Cell, r.Outcome, r.Detail));

            if (r.Outcome is not (CellWriteOutcome.Written or CellWriteOutcome.AlreadyMatches
                                  or CellWriteOutcome.CellUnavailable))
                return Stop($"{a.Cell} — {r.Detail}", records);
        }

        var written = records.Count(r => r.Outcome == CellWriteOutcome.Written);
        if (written == 0)
            return new(weekStart, WeekPhase.NothingToDo, 0, skipped + records.Count, "바뀐 칸이 없습니다.", records);

        // ④ 저장
        progress?.Report(new($"{weekStart:MM-dd} 주를 저장하고 있어요…"));

        var save = await saver.SaveAsync(ct);
        if (save.Outcome != SaveOutcome.Saved)
            return Stop($"저장하지 못했습니다 — {save.Detail}", records);

        // ⑤ 재조회 검증 — 저장했다는 말을 믿지 않는다
        progress?.Report(new($"{weekStart:MM-dd} 주 저장 결과를 확인하고 있어요…"));

        var mismatch = await VerifyAsync(weekStart, targets, request, ct);
        if (mismatch is not null) return Stop($"저장 후 값이 다릅니다 — {mismatch}", records);

        return new(weekStart, WeekPhase.Completed, written, skipped, "저장·검증 완료", records);
    }

    /// <summary>지금 화면의 값으로 이 주의 계획을 다시 만든다.</summary>
    private async Task<TimetablePlan> PlanFromScreenAsync(
        IReadOnlyList<TimetableSourceLesson> lessons, TimetableRunRequest request)
    {
        var snapshot = await reader.ReadCurrentWeekAsync();

        var current = snapshot.Cells
            .Where(c => c.Value.Length > 0)
            .ToDictionary(c => c.Key, c => TimetableMenuParser.Parse(c.Value).StableKey);

        // 못 쓰는 칸은 미리 알 수 없다 — 쓰기가 시도하면서 CellUnavailable 로 알려 준다
        var screen = new TimetableScreenState(
            current, new HashSet<TimetableCell>(), request.TermStart, request.TermEnd);

        return TimetablePlanBuilder.Build(
            lessons, request.Rules, request.Catalog, screen, request.AllowOverwrite);
    }

    /// <summary>
    /// 다른 주로 갔다가 돌아와 다시 읽고, 넣은 칸이 그대로 남아 있는지 본다.
    /// 화면에 보이는 값을 그 자리에서 다시 읽으면 저장이 실제로 됐는지 알 수 없다.
    /// 어긋난 칸의 설명을 돌려준다. 다 맞으면 null.
    /// </summary>
    private async Task<string?> VerifyAsync(
        DateOnly weekStart, IReadOnlyList<TimetableAssignment> targets,
        TimetableRunRequest request, CancellationToken ct)
    {
        var weeks = await reader.ReadWeeksAsync();
        var mine = weeks.FirstOrDefault(w => weekStart >= w.Start && weekStart <= w.End);
        if (mine is null) return "주차 목록에서 이 주를 찾지 못했습니다.";

        var other = weeks.FirstOrDefault(w => w.Index != mine.Index);
        if (other is not null) await reader.SelectWeekAsync(other.Index);
        await reader.SelectWeekAsync(mine.Index);

        ct.ThrowIfCancellationRequested();

        var snapshot = await reader.ReadCurrentWeekAsync();

        foreach (var a in targets)
        {
            snapshot.Cells.TryGetValue(a.Cell, out var raw);
            if (string.IsNullOrEmpty(raw)) return $"{a.Cell} 이 비어 있습니다.";

            var target = request.Catalog.Find(a.TargetStableKey);
            var actual = TimetableMenuParser.Parse(raw).StableKey;

            if (target is null || !TimetablePlanBuilder.SameValue(actual, target, request.Catalog))
                return $"{a.Cell} 이 목표와 다릅니다.";
        }

        return null;
    }
}
