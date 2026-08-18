using System.IO;
using System.Windows;
using Microsoft.Win32;
using NeisAutoFill.App.Services;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Timetable;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// 연간 시간표 자동입력의 전체 흐름을 한 곳에 모은다 (BatchUploadFlow 와 같은 역할).
///
/// <code>
/// 파일 고르기 → 문서 해석 → 검토 창 → 나이스 준비·카탈로그 → 매핑 창 → 실행 계획
/// </code>
///
/// <b>아직 실제 입력은 하지 않는다.</b> 계획까지 만들어 보여 주고 멈춘다 —
/// 한 주 전체 입력(W6)과 저장(S1)이 실기기에서 승급된 뒤에 이어 붙인다.
/// </summary>
public sealed class TimetableFlow(TimetableSession session, Action<string> log)
{
    /// <summary>흐름 한 번의 결과.</summary>
    /// <param name="Plan">만들어진 실행 계획 (중간에 멈추면 null)</param>
    /// <param name="Message">사용자에게 보여 줄 마무리 문구</param>
    public sealed record Result(TimetablePlan? Plan, string Message);

    /// <param name="timetableFile">비워 두면 파일 선택 창을 띄운다 (재현·시험할 때 경로를 직접 준다)</param>
    /// <param name="creativeFile">창체 계획 — 없어도 된다</param>
    public async Task<Result> RunAsync(
        Window? owner, IProgress<ProgressInfo>? progress = null,
        string? timetableFile = null, string? creativeFile = null)
    {
        // ── ① 원본 문서 ─────────────────────────────────────────
        var timetablePath = timetableFile ?? AskFile("연간 시간표 파일 선택");
        if (timetablePath is null) return new(null, "취소했습니다.");

        var creativePath = creativeFile ?? (timetableFile is null
            ? AskFile("창의적 체험활동 계획 (없으면 취소)")
            : null);

        log($"문서를 읽는 중… {Path.GetFileName(timetablePath)}");

        TimetableSourcePackage source;
        CreativeSourcePackage? creative = null;
        try
        {
            source = await Task.Run(() =>
                TimetableDocumentParser.Parse(PdfLayoutExtractor.ExtractAny(timetablePath)));

            if (creativePath is not null)
                creative = await Task.Run(() =>
                    CreativeDocumentParser.Parse(PdfLayoutExtractor.ExtractAny(creativePath)));
        }
        catch (Exception ex)
        {
            return new(null, $"문서를 읽지 못했습니다.\n{ex.Message}");
        }

        if (source.Lessons.Count == 0)
            return new(null, "문서에서 수업을 하나도 찾지 못했습니다. 다른 형식(PDF)으로 저장해 넣어 보세요.");

        log($"수업 {source.Lessons.Count}칸 · 경고 {source.Warnings.Count}건");

        // ── ② 창체 병합·연결 ────────────────────────────────────
        IReadOnlyList<CreativeLink> links = Array.Empty<CreativeLink>();
        IReadOnlyList<string> pairProblems = Array.Empty<string>();

        if (creative is not null)
        {
            var merged = CreativeActivityMerger.Merge(creative.Events);
            links = CreativeActivityLinker.Link(source.Lessons, merged.Merged);
            pairProblems = CreativeActivityLinker.CheckPair(source, creative);

            log($"창체 {creative.Events.Count}건 → 병합 {merged.Merged.Count}건 · " +
                $"충돌 {merged.Conflicts.Count}건 · 연결 {links.Count(l => l.IsResolved)}/{links.Count}칸");
        }

        // ── ③ 검토 창 — 사람이 확인하고 고친다 ───────────────────
        var reviewed = TimetableReviewWindow.Ask(
            new TimetableReviewViewModel(source, links, pairProblems), owner);
        if (reviewed is null) return new(null, "검토를 취소했습니다.");

        log($"검토 완료 — {reviewed.Count}칸");

        // ── ④ 나이스 준비 (읽기 전용) ───────────────────────────
        var target = reviewed.Min(l => l.Cell.Date);
        var pre = await session.PreflightAsync(target, progress);
        if (!pre.Ok) return new(null, pre.Message);

        log(pre.Message);

        // ── ⑤ 매핑 창 ──────────────────────────────────────────
        var rules = session.OpenMapping(reviewed, owner);
        if (rules is null) return new(null, "매핑을 취소했습니다.");

        log($"매핑 규칙 {rules.Count}건 확정");

        // ── ⑥ 실행 계획 ────────────────────────────────────────
        var plan = TimetablePlanBuilder.Build(reviewed, rules, session.Catalog!, session.ScreenState());
        return new(plan, Describe(plan));
    }

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

    private static string? AskFile(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "시간표 문서|*.pdf;*.hwp;*.hwpx|PDF|*.pdf|한글|*.hwp;*.hwpx|모든 파일|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
