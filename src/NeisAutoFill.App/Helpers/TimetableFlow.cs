using System.Windows;
using NeisAutoFill.App.Services;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// 연간 시간표 자동입력의 전체 흐름을 한 곳에 모은다 (BatchUploadFlow 와 같은 역할).
///
/// <code>
/// (시간표 탭에서 문서·기간·교사를 정한 뒤) 나이스 준비·카탈로그 → 실행 계획
///   → 주 단위 입력·저장·검증 → 결과 창
/// </code>
///
/// 되돌릴 수 없는 첫 행동은 탭의 <b>[입력 시작]</b>이다. 누르면 바로 시작하고,
/// 그 버튼은 <b>[작업 중지]</b>로 바뀐다 — 확인 창을 한 번 더 띄우지 않는다.
/// </summary>
public sealed class TimetableFlow(TimetableSession session, Action<string> log)
{
    /// <summary>흐름 한 번의 결과.</summary>
    /// <param name="Plan">만들어진 실행 계획 (중간에 멈추면 null)</param>
    /// <param name="Message">사용자에게 보여 줄 마무리 문구</param>
    public sealed record Result(TimetablePlan? Plan, string Message);

    /// <summary>
    /// 탭에서 <b>교사 배정까지 끝낸</b> 상태로 실행한다 — 매핑 창을 다시 띄우지 않는다.
    /// </summary>
    /// <param name="rules">탭에서 만든 규칙 (기본 + 정기 예외 + 비정기 예외)</param>
    public async Task<Result> RunPreparedAsync(
        IReadOnlyList<TimetableSourceLesson> lessons,
        IReadOnlyList<TimetableMappingRule> rules,
        TimetableRangeChoice range,
        Window? owner,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        if (lessons.Count == 0) return new(null, "넣을 수업이 없습니다.");

        var pre = await session.PreflightAsync(lessons.Min(l => l.Cell.Date), progress, ct);
        if (!pre.Ok) return new(null, pre.Message);

        log(pre.Message);

        var inTerm = InTerm(lessons);
        if (inTerm.Count == 0)
            return new(null,
                $"고른 기간({range.From:yyyy-MM-dd}~{range.To:yyyy-MM-dd})이 나이스에서 조회한 학기" +
                $"({session.TermStart:yyyy-MM-dd}~{session.TermEnd:yyyy-MM-dd})와 겹치지 않습니다.\n" +
                "문서의 학년도·학기와 나이스에서 고른 학년도·학기를 확인해 주세요.");

        if (inTerm.Count < lessons.Count)
            log($"나이스 학기 밖 {lessons.Count - inTerm.Count}칸은 제외합니다.");

        var plan = TimetablePlanBuilder.Build(
            inTerm, rules, session.Catalog!, session.ScreenState(), range.AllowOverwrite);
        log(Describe(plan));

        if (!plan.CanRun) return new(plan, Describe(plan));

        return await RunPlanAsync(plan, inTerm, rules, range, owner, progress, ct);
    }

    /// <summary>나이스가 아는 학기 안의 수업만. 학기 밖 날짜는 화면에 존재하지도 않는다.</summary>
    private IReadOnlyList<TimetableSourceLesson> InTerm(IEnumerable<TimetableSourceLesson> lessons) =>
        lessons.Where(l =>
            (session.TermStart is null || l.Cell.Date >= session.TermStart) &&
            (session.TermEnd is null || l.Cell.Date <= session.TermEnd)).ToList();

    /// <summary>실제로 입력·저장한다. 멈추면 결과 창에서 이어서 다시 시도할 수 있다.</summary>
    private async Task<Result> RunPlanAsync(
        TimetablePlan plan,
        IReadOnlyList<TimetableSourceLesson> lessons,
        IReadOnlyList<TimetableMappingRule> rules,
        TimetableRangeChoice range,
        Window? owner,
        IProgress<ProgressInfo>? progress,
        CancellationToken ct)
    {
        var checkpoint = session.ResumePoint(plan, out var blocker);
        if (blocker is not null) log($"이전 기록을 이어서 쓰지 않습니다 — {blocker}");

        log($"입력을 시작합니다 — {range.From:yyyy-MM-dd}~{range.To:yyyy-MM-dd} · 주 {plan.ByWeek.Count}개" +
            (range.AllowOverwrite ? " · 기존 값 덮어씀" : ""));

        while (true)
        {
            var result = await session.RunBatchAsync(
                plan, lessons, rules, checkpoint, range.AllowOverwrite, progress, ct);
            checkpoint = result.Checkpoint;

            foreach (var week in result.Weeks) log(week.Describe());

            // 사용자가 중지했으면 결과 창을 띄우지 않는다 — 스스로 멈춘 것을 다시 물을 이유가 없다
            if (ct.IsCancellationRequested) return new(plan, Summarize(result));

            // 멈췄으면 사용자가 문제를 해결하고 [이어서 다시 시도]를 누를 수 있다
            if (!TimetableResultWindow.AskRetry(result, owner))
                return new(plan, Summarize(result));

            log("이어서 다시 시도합니다…");
        }
    }

    private static string Summarize(BatchRunResult result) =>
        result.Completed
            ? $"연간 입력을 마쳤습니다 — {result.TotalWritten}칸 입력·저장"
            : $"{result.StoppedReason}\n여기까지 저장된 것은 그대로 남아 있습니다 ({result.Checkpoint.Describe()}).";

    /// <summary>계획을 사람 말로 요약한다. 막힌 것이 있으면 그것부터 알린다.</summary>
    private static string Describe(TimetablePlan plan)
    {
        var lines = new List<string>
        {
            $"전체 {plan.Assignments.Count}칸 · 입력 예정 {plan.Writable.Count}칸 · 주 {plan.ByWeek.Count}개",
        };

        foreach (var kv in plan.CountByStatus.OrderByDescending(k => k.Value))
            lines.Add($"  {Korean(kv.Key)} {kv.Value}칸");

        lines.Add(plan.CanRun
            ? "\n막힌 항목이 없습니다. 실제 입력은 다음 단계에서 이어집니다."
            : $"\n먼저 풀어야 할 항목이 {plan.Blocking.Count}칸 있습니다.");

        return string.Join("\n", lines);
    }

    private static string Korean(AssignmentStatus s) => s switch
    {
        AssignmentStatus.Pending => "입력 예정",
        AssignmentStatus.AlreadyMatches => "이미 같음",
        AssignmentStatus.Skipped => "입력 안 함",
        AssignmentStatus.SourceUnresolved => "원본 미해결",
        AssignmentStatus.MappingUnresolved => "매핑 미해결",
        AssignmentStatus.MappingConflict => "매핑 충돌",
        AssignmentStatus.CreativeUnresolved => "창체 미분류",
        AssignmentStatus.OptionNotFound => "대상 없음",
        AssignmentStatus.ExistingValueConflict => "기존 값 충돌",
        AssignmentStatus.CellUnavailable => "수업 없는 날",
        AssignmentStatus.DuplicateTarget => "중복 목표",
        _ => s.ToString(),
    };
}
