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
/// (시간표 탭에서 문서·기간을 정한 뒤) 나이스 준비·카탈로그 → 매핑 창 → 실행 계획
///   → 동의 창 → 주 단위 입력·저장·검증 → 결과 창
/// </code>
///
/// 계획까지는 나이스를 <b>읽기만</b> 한다. 되돌릴 수 없는 첫 행동은 동의 창의 [입력 시작]이다.
/// </summary>
public sealed class TimetableFlow(TimetableSession session, Action<string> log)
{
    /// <summary>흐름 한 번의 결과.</summary>
    /// <param name="Plan">만들어진 실행 계획 (중간에 멈추면 null)</param>
    /// <param name="Message">사용자에게 보여 줄 마무리 문구</param>
    public sealed record Result(TimetablePlan? Plan, string Message);

    /// <summary>
    /// 이미 읽어 둔 수업과 기간으로 <b>나이스 준비부터</b> 진행한다 (시간표 탭에서 쓴다).
    ///
    /// 파일 고르기·검토·기간 선택은 탭 화면이 이미 담당하므로 여기서 다시 묻지 않는다.
    /// </summary>
    public async Task<Result> RunLessonsAsync(
        IReadOnlyList<TimetableSourceLesson> lessons,
        TimetableRangeChoice range,
        Window? owner,
        IProgress<ProgressInfo>? progress = null)
    {
        if (lessons.Count == 0) return new(null, "넣을 수업이 없습니다.");

        // ── 나이스 준비 (읽기 전용) ─────────────────────────────
        var pre = await session.PreflightAsync(lessons.Min(l => l.Cell.Date), progress);
        if (!pre.Ok) return new(null, pre.Message);

        log(pre.Message);

        // 나이스가 아는 학기 밖이면 한 칸도 넣을 수 없다 — 매핑까지 가기 전에 멈춘다
        var inTerm = lessons.Where(l =>
            (session.TermStart is null || l.Cell.Date >= session.TermStart) &&
            (session.TermEnd is null || l.Cell.Date <= session.TermEnd)).ToList();

        if (inTerm.Count == 0)
            return new(null,
                $"고른 기간({range.From:yyyy-MM-dd}~{range.To:yyyy-MM-dd})이 나이스에서 조회한 학기" +
                $"({session.TermStart:yyyy-MM-dd}~{session.TermEnd:yyyy-MM-dd})와 겹치지 않습니다.\n" +
                "문서의 학년도·학기와 나이스에서 고른 학년도·학기를 확인해 주세요.");

        if (inTerm.Count < lessons.Count)
            log($"나이스 학기 밖 {lessons.Count - inTerm.Count}칸은 제외합니다.");

        // ── 매핑 ───────────────────────────────────────────────
        var rules = session.OpenMapping(inTerm, owner);
        if (rules is null) return new(null, "매핑을 취소했습니다.");

        log($"매핑 규칙 {rules.Count}건 확정");

        // ── 실행 계획 ──────────────────────────────────────────
        var plan = TimetablePlanBuilder.Build(
            inTerm, rules, session.Catalog!, session.ScreenState(), range.AllowOverwrite);
        log(Describe(plan));

        if (!plan.CanRun) return new(plan, Describe(plan));

        return await RunPlanAsync(plan, inTerm, rules, range, owner, progress);
    }

    /// <summary>
    /// 동의를 받고 실제로 입력·저장한다. 멈추면 결과 창에서 이어서 다시 시도할 수 있다.
    /// </summary>
    private async Task<Result> RunPlanAsync(
        TimetablePlan plan,
        IReadOnlyList<TimetableSourceLesson> lessons,
        IReadOnlyList<TimetableMappingRule> rules,
        TimetableRangeChoice range,
        Window? owner,
        IProgress<ProgressInfo>? progress)
    {
        var checkpoint = session.ResumePoint(plan, out var blocker);
        if (blocker is not null) log($"이전 기록을 이어서 쓰지 않습니다 — {blocker}");

        if (!TimetableRunConsentWindow.Ask(plan, checkpoint, blocker, range, owner))
            return new(plan, "입력을 취소했습니다. 나이스는 그대로입니다.");

        while (true)
        {
            var result = await session.RunBatchAsync(
                plan, lessons, rules, checkpoint, range.AllowOverwrite, progress);
            checkpoint = result.Checkpoint;

            foreach (var week in result.Weeks) log(week.Describe());

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
