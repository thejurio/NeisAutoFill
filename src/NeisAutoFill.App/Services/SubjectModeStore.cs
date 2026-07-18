using System.IO;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Excel;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 전담 자료(반별 명단·학년별 계획) 저장·로드 (F9 M4a). 담임 IO(PlanWorkbook*)를 재사용한다.
/// - 명단: 전담\명단\{학년}-{반}.xlsx  — 계획 없이 [학생명단] 시트만
/// - 계획: 전담\평가계획\{학년}학년.xlsx — 과목 시트들(담임 평가계획서 포맷)
/// </summary>
public sealed class SubjectModeStore
{
    private readonly GradeScale _scale;
    public SubjectModeStore(GradeScale scale) => _scale = scale;

    private static string Ws => AppPaths.EnsureWorkspaceRoot();

    // ── 명단 (반별) ──
    /// <summary>등록된 반 목록 (명단 폴더 스캔). 학년·반 오름차순.</summary>
    public IReadOnlyList<ClassRef> ListClasses()
    {
        var dir = Path.Combine(Ws, SubjectModePaths.RootFolder, "명단");
        if (!Directory.Exists(dir)) return Array.Empty<ClassRef>();
        var list = new List<ClassRef>();
        foreach (var f in Directory.GetFiles(dir, "*.xlsx"))
            if (SubjectModePaths.ParseRosterName(Path.GetFileNameWithoutExtension(f)) is { } c)
                list.Add(c);
        return list.OrderBy(c => c.Grade).ThenBy(c => c.Class).ToList();
    }

    public IReadOnlyList<(string No, string Name)> LoadRoster(ClassRef c)
    {
        var path = SubjectModePaths.RosterFile(Ws, c);
        return File.Exists(path) ? PlanWorkbookLoader.LoadRoster(path) : Array.Empty<(string, string)>();
    }

    public void SaveRoster(ClassRef c, IReadOnlyList<(string No, string Name)> roster)
    {
        var path = SubjectModePaths.RosterFile(Ws, c);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 명단만 담는다 (계획은 학년 파일에). 빈 계획 + 명단 시트.
        PlanWorkbookWriter.Write(path, Array.Empty<SubjectPlan>(), roster, _scale);
    }

    // ── 계획 (학년별, 과목=시트) ──
    /// <summary>등록된 학년 목록 (계획 폴더 스캔). 오름차순.</summary>
    public IReadOnlyList<int> ListGrades()
    {
        var dir = Path.Combine(Ws, SubjectModePaths.RootFolder, "평가계획");
        if (!Directory.Exists(dir)) return Array.Empty<int>();
        var list = new List<int>();
        foreach (var f in Directory.GetFiles(dir, "*학년.xlsx"))
            if (SubjectModePaths.ParsePlanName(Path.GetFileNameWithoutExtension(f)) is { } g)
                list.Add(g);
        return list.OrderBy(g => g).ToList();
    }

    public IReadOnlyList<SubjectPlan> LoadPlan(int grade)
    {
        var path = SubjectModePaths.PlanFile(Ws, grade);
        return File.Exists(path) ? PlanWorkbookLoader.Load(path, _scale) : Array.Empty<SubjectPlan>();
    }

    public void SavePlan(int grade, IReadOnlyList<SubjectPlan> plans)
    {
        var path = SubjectModePaths.PlanFile(Ws, grade);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 계획만 담는다 (명단은 반 파일에). 과목 시트들 + 빈 명단.
        PlanWorkbookWriter.Write(path, plans, Array.Empty<(string, string)>(), _scale);
    }
}
