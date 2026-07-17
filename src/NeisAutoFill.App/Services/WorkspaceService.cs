using System.IO;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Excel;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 작업 자료(성적·평가계획 파일)의 수명 전담 — 경로 관리, 읽기/쓰기, 최근 파일, 동기화 계산.
/// UI 관심사(Subjects 컬렉션·다이얼로그·dirty 추적)는 갖지 않는다 — MainViewModel 이 결과를 받아 반영.
/// 모든 메서드는 예외를 던지지 않고 (Ok, Error) 로 반환한다.
/// </summary>
public sealed class WorkspaceService(IScaleStore scales, AppStateStore appState)
{
    // ── 상태 ─────────────────────────────
    public IReadOnlyList<SubjectPlan> Plans { get; private set; } = Array.Empty<SubjectPlan>();
    public IReadOnlyList<(string No, string Name)> Roster { get; private set; } = Array.Empty<(string, string)>();

    /// <summary>명단이 확정 정보인지 — 계획서에 [학생명단] 시트가 있으면 그 내용(비어 있어도)이 전부.
    /// 사용자가 명단을 전부 지운 경우를 "정보 없음"과 구분한다.</summary>
    public bool RosterAuthoritative { get; private set; }
    public string? GradeFilePath { get; private set; }
    public string? PlanFilePath { get; private set; }

    public string DefaultGradePath => Path.Combine(AppPaths.EnsureWorkspace(), "성적.xlsx");
    public string DefaultPlanPath => Path.Combine(AppPaths.EnsureWorkspace(), "평가계획서.xlsx");

    /// <summary>시작 복원용 마지막 경로 (실존 파일만).</summary>
    public string? LastGradePath =>
        appState.State.LastGradePath is { } p && File.Exists(p) ? p : null;
    public string? LastPlanPath =>
        appState.State.LastPlanPath is { } p && File.Exists(p) ? p : null;

    /// <summary>최근 파일 메뉴 항목 (실존 파일만, 평가계획서·성적파일 구분).</summary>
    public IReadOnlyList<(string Path, string Display, bool IsPlan)> RecentEntries =>
        appState.ExistingRecentPlans().Select(p => (p, Path.GetFileName(p), true))
        .Concat(appState.ExistingRecentGrades().Select(p => (p, Path.GetFileName(p), false)))
        .ToList();

    // ── 읽기 ─────────────────────────────

    /// <summary>성적파일 읽기. 성공 시 시트 목록 반환 + 경로·최근 기록 갱신.</summary>
    public (IReadOnlyList<SubjectSheet>? Sheets, string? Error) LoadGrades(string path)
    {
        try
        {
            var sheets = WorkbookLoader.Load(path);
            if (sheets.Count == 0) return (null, "번호/이름 컬럼이 있는 시트를 찾지 못했습니다.");
            GradeFilePath = path;
            appState.TouchGrade(path);
            return (sheets, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>평가계획서 읽기. 성공 시 Plans/Roster 갱신 + 경로·최근 기록.</summary>
    public string? LoadPlan(string path)
    {
        try
        {
            Plans = PlanWorkbookLoader.Load(path, scales.Active);
            Roster = PlanWorkbookLoader.LoadRoster(path);
            RosterAuthoritative = PlanWorkbookLoader.HasRosterSheet(path);
            PlanFilePath = path;
            appState.TouchPlan(path);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>인앱 편집 결과 저장 (평가계획서 엑셀) — 성공 시 저장 경로 반환.</summary>
    public (string? Path, string? Error) SavePlan(
        IReadOnlyList<SubjectPlan> plans, IReadOnlyList<(string, string)> roster)
    {
        var path = PlanFilePath ?? DefaultPlanPath;
        try
        {
            PlanWorkbookWriter.Write(path, plans, roster, scales.Active);
            return (path, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── 쓰기 ─────────────────────────────

    /// <summary>현재 시트를 지정 경로(기본: 작업 파일)에 저장. 성공 시 경로 기록.</summary>
    public string? SaveGrades(IReadOnlyList<SubjectSheet> sheets, string? path = null)
    {
        path ??= GradeFilePath ?? DefaultGradePath;
        try
        {
            GradeWorkbookWriter.Write(path, sheets);
            GradeFilePath = path;
            appState.TouchGrade(path);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── 명단·계획 → 성적표 동기화 (계산만 — 반영·저장은 호출자) ──

    /// <summary>
    /// 현재 시트들을 명단·계획에 맞춘 새 시트 목록으로. 바뀐 게 없으면 null.
    /// 계획에만 있는 새 과목은 뒤에 추가된다. (규칙: Core/SheetSynchronizer)
    /// </summary>
    public IReadOnlyList<SubjectSheet>? ComputeSync(IReadOnlyList<SubjectSheet> current)
    {
        if (Roster.Count == 0 && Plans.Count == 0 && !RosterAuthoritative) return null;

        bool changed = false;
        var planByName = Plans.ToDictionary(p => p.SubjectName);
        var currentNames = current.Select(s => s.SubjectName).ToHashSet();
        var rebuilt = new List<SubjectSheet>();

        foreach (var sheet in current)
        {
            var areas = planByName.TryGetValue(sheet.SubjectName, out var plan) ? plan.Domains : sheet.Areas;
            var newSheet = SheetSynchronizer.BuildSheet(sheet.SubjectName, areas, sheet, Roster, RosterAuthoritative);
            if (SheetSynchronizer.ShapeEquals(sheet, newSheet)) { rebuilt.Add(sheet); continue; }
            rebuilt.Add(newSheet);
            changed = true;
        }
        foreach (var plan in Plans.Where(p => !currentNames.Contains(p.SubjectName)))
        {
            rebuilt.Add(SheetSynchronizer.BuildSheet(plan.SubjectName, plan.Domains, null, Roster, RosterAuthoritative));
            changed = true;
        }
        return changed ? rebuilt : null;
    }

    /// <summary>계획+명단만으로 새 성적표 구성 (파일 생성 전 단계). 재료가 없으면 null.</summary>
    public IReadOnlyList<SubjectSheet>? BuildFreshSheets()
    {
        if (Plans.Count == 0 || Roster.Count == 0) return null;
        var students = Roster
            .Select(r => new Student(r.No, r.Name, new Dictionary<string, string>(), null))
            .ToList();
        return Plans.Select(p => new SubjectSheet(p.SubjectName, p.Domains, students)).ToList();
    }
}
