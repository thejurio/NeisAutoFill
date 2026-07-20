using System.Windows;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// 입력 전 매칭 확인의 단일 창구 (R8) — 등급·서술문 공용.
/// 화면 파악 결과(MatchContext)를 분석해, 문제 없으면 조용히 진행하고
/// 필요할 때만 확인을 단계별(과목 메시지창 → 학생 이름창 → 영역창)로 띄운다.
///
/// 배치(전과목/전체반) 원칙: 같은 반이면 이름 매핑은 첫 대상에서 1회만 받고
/// 이후 재사용한다 ('입력 안 함'="" 포함). Reset() 으로 배치 시작 시 캐시를 비운다.
/// </summary>
internal sealed class MatchSession
{
    private readonly Action<string> _log;
    private IReadOnlyDictionary<string, string>? _nameMap;   // 화면이름→엑셀이름 ("" = 입력 안 함)
    private IReadOnlyDictionary<string, string>? _acceptedSubjects;   // 프로그램 과목 → 사용자가 매핑한 화면 과목

    public MatchSession(Action<string> log) => _log = log;

    /// <summary>배치 시작 시 호출 — 이름 매핑 캐시를 비운다 (첫 대상에서 새로 받음).</summary>
    public void Reset() { _nameMap = null; _acceptedSubjects = null; }

    /// <summary>배치 선택 창에서 사용자가 이미 확정한 (프로그램 과목 → 화면 과목) 매핑.
    /// 화면 과목이 매핑값 그대로면 과목 확인을 다시 묻지 않는다 — 같은 질문 이중 팝업 방지.</summary>
    public void AcceptSubjects(IReadOnlyDictionary<string, string> displayToScreen) =>
        _acceptedSubjects = displayToScreen;

    private bool SubjectPreAccepted(MatchContext ctx) =>
        _acceptedSubjects is not null &&
        _acceptedSubjects.TryGetValue(ctx.TargetSubject, out var screen) &&
        screen == ctx.ScreenSubject;

    /// <summary>등급 입력용 매칭 콜백. batch=true 면 이름 매핑을 캐시하고 재사용한다.</summary>
    public Func<MatchContext, Task<MatchDecision?>> ForGrades(SubjectSheet sheet, bool batch = false) => ctx =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var issues = MatchAnalyzer.Analyze(
                ctx.ScreenSubject, ctx.TargetSubject, ctx.RowMap, sheet.Students, sheet.Areas);
            bool subjectOk = SubjectPreAccepted(ctx);   // 선택 창에서 이미 매핑한 과목이면 안 묻는다
            if (issues.Clean)
                return new MatchDecision(StudentMatcher.MatchMode.ByName, NameMap: batch ? _nameMap : null);

            // 과목명만 다르고 학생·영역은 정상 → 매핑 창 대신 "그래도 진행?" 만 묻는다
            if (issues.SubjectOnlyMismatch)
                return subjectOk || ConfirmSubjectOnly(issues.ScreenSubject, sheet.SubjectName)
                    ? new MatchDecision(StudentMatcher.MatchMode.ByName, NameMap: batch ? _nameMap : null)
                    : null;

            // 배치: 이름 불일치뿐이고 캐시가 전원을 덮으면 창 없이 재사용
            if (batch && issues.AreasClean && issues.NamesCoveredBy(_nameMap))
                return new MatchDecision(StudentMatcher.MatchMode.ByName, NameMap: _nameMap);

            var vm = new MatchPreviewViewModel(issues, sheet, batch ? _nameMap : null);
            if (subjectOk) vm.SubjectAccepted = true;
            if (!ShowSteps(vm)) return null;
            var decision = vm.BuildDecision();
            if (batch) MergeCache(decision?.NameMap);
            return decision;
        }).Task;

    /// <summary>서술문 입력용 매칭 콜백 (영역 없음 — 과목·이름만).</summary>
    public Func<MatchContext, Task<MatchDecision?>> ForNarratives(
        IReadOnlyList<NarrativeEntry> entries, bool batch = false) => ctx =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var issues = MatchAnalyzer.AnalyzeNarratives(
                ctx.ScreenSubject, ctx.TargetSubject, ctx.RowMap, entries.Select(e => e.Name).ToList());
            bool subjectOk = SubjectPreAccepted(ctx);   // 선택 창에서 이미 매핑한 과목이면 안 묻는다
            if (issues.Clean)
                return new MatchDecision(StudentMatcher.MatchMode.ByName, NameMap: batch ? _nameMap : null);

            if (issues.SubjectOnlyMismatch)
                return subjectOk || ConfirmSubjectOnly(issues.ScreenSubject, ctx.TargetSubject)
                    ? new MatchDecision(StudentMatcher.MatchMode.ByName, NameMap: batch ? _nameMap : null)
                    : (MatchDecision?)null;

            if (batch && issues.NamesCoveredBy(_nameMap))
                return new MatchDecision(StudentMatcher.MatchMode.ByName, NameMap: _nameMap);

            // 확인 창 재사용 — 합성 sheet 로 학생 후보 제공 (영역은 없음)
            var sheet = new SubjectSheet(ctx.TargetSubject, Array.Empty<string>(),
                entries.Select(e => new Student(e.No, e.Name, new Dictionary<string, string>())).ToList());
            var vm = new MatchPreviewViewModel(issues, sheet, batch ? _nameMap : null);
            if (subjectOk) vm.SubjectAccepted = true;
            if (!ShowSteps(vm)) return null;
            var decision = vm.BuildDecision();
            if (batch) MergeCache(decision?.NameMap);
            return decision;
        }).Task;

    /// <summary>과목만 다를 때의 간단 확인.</summary>
    private static bool ConfirmSubjectOnly(string? screenSubject, string targetSubject) =>
        MessageBox.Show(
            $"화면 과목은 '{screenSubject}'인데 입력 대상은 '{targetSubject}'입니다.\n" +
            "학생·영역은 화면과 일치합니다. 이 화면에 그대로 입력할까요?",
            "과목 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>확인을 단계별 창으로 — 과목 메시지창 → 학생 이름창 → 영역창. 취소하면 false.</summary>
    private static bool ShowSteps(MatchPreviewViewModel vm)
    {
        if (vm.HasSubjectWarning && !vm.SubjectAccepted)
        {
            var ok = MessageBox.Show(
                vm.SubjectWarning + "\n\n이 화면에 그대로 입력할까요?",
                "과목 확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return false;
            vm.SubjectAccepted = true;
        }
        if (vm.ShowStudentSection)
        {
            vm.CurrentStep = MatchPreviewViewModel.Step.Student;
            if (new MatchPreviewWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return false;
        }
        if (vm.HasAreaIssues)
        {
            vm.CurrentStep = MatchPreviewViewModel.Step.Area;
            if (new MatchPreviewWindow(vm) { Owner = Application.Current.MainWindow }.ShowDialog() != true) return false;
        }
        return true;
    }

    /// <summary>이번에 받은 이름 매핑을 캐시에 병합 — 다음 대상부터 재사용.</summary>
    private void MergeCache(IReadOnlyDictionary<string, string>? nm)
    {
        if (nm is null) return;
        var merged = new Dictionary<string, string>(_nameMap ?? new Dictionary<string, string>());
        foreach (var kv in nm) merged[kv.Key] = kv.Value;
        _nameMap = merged;
    }
}
