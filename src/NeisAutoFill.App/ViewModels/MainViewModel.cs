using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NeisAutoFill.App.Helpers;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.App.Services;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Excel;

namespace NeisAutoFill.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly INeisEngine _engine;
    private readonly IScaleStore _scales;
    private readonly GeneratorSettingsStore _generatorSettings;
    private readonly NarrativeStore _narratives;
    private readonly AppStateStore _appState;
    private readonly IProgress<ProgressInfo> _progress;
    private CancellationTokenSource? _cts;
    private GeneratorViewModel? _generatorVm;   // 생성 결과 보존을 위해 단일 인스턴스 유지

    private readonly Automation.EngineOptions _engineOptions;
    private readonly System.Windows.Threading.DispatcherTimer _autoSaveTimer;

    private readonly GenerationQueue _generationQueue;
    private readonly NarrativeMirror _narrativeMirror;
    private readonly WorkspaceService _workspace;   // 자료 파일 수명 전담 (경로·IO·계획/명단 상태)
    private readonly ProfileStore _profiles;        // 학급 모드(담임/전담)

    public MainViewModel(INeisEngine engine, IScaleStore scales,
        GeneratorSettingsStore generatorSettings, NarrativeStore narratives,
        AppStateStore appState, GenerationQueue generationQueue, NarrativeMirror narrativeMirror,
        WorkspaceService workspace,
        Automation.EngineOptions engineOptions,
        ProfileStore profiles,
        NeisSessionController session,
        TimetableSession timetable,
        EvalPlanSession evalPlan)
    {
        _session = session;
        _timetable = timetable;
        _engine = engine;
        _scales = scales;
        _generatorSettings = generatorSettings;
        _narratives = narratives;
        _appState = appState;
        _generationQueue = generationQueue;
        _narrativeMirror = narrativeMirror;
        _workspace = workspace;
        _engineOptions = engineOptions;
        _profiles = profiles;

        _generationQueue.Log += Log;      // 배치 시작·완료·중지를 메인 로그에도
        _generationQueue.StateChanged += () => OnPropertyChanged(nameof(GenerationStatus));
        _narrativeMirror.Log += Log;      // 미러 실패 안내

        // 편집 후 2초 조용하면 자동 저장 (파일 잠금 등 실패 시 dirty 유지 → 다음 편집·종료 때 재시도)
        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); AutoSaveGrades(); };

        // 저장된 지역 복원 → 접속 주소 반영
        _selectedRegion = NeisRegions.Find(generatorSettings.Options.NeisRegionCode);
        _engineOptions.NeisUrl = _selectedRegion.Url;

        // Progress<T> 는 생성 시점(UI 스레드)의 SynchronizationContext 로 콜백을 마셜링
        _progress = new Progress<ProgressInfo>(OnProgress);

        LaunchEdgeCommand = new RelayCommand(LaunchEdge);
        OpenExcelCommand = new RelayCommand(OpenExcel);
        OpenScaleEditorCommand = new RelayCommand(OpenScaleEditor);
        OpenRecentCommand = new RelayCommand<string>(p => { if (p is not null) LoadExcel(p); });
        OpenPlanEditorCommand = new RelayCommand(() => OpenPlanEditor());
        RunAllSubjectsCommand = new AsyncRelayCommand(RunAllSubjectsAsync);
        InspectCommand = new AsyncRelayCommand(InspectAsync);
        ExportGradesCommand = new RelayCommand(ExportGrades);
        HelpCommand = new AsyncRelayCommand(OpenHelpAsync);

        _showCriteriaPanel = appState.State.ShowCriteriaPanel;
        _logExpanded = appState.State.LogExpanded;

        Timetable = new TimetableTabViewModel(_timetable, Log, _progress, () => IsConnected);
        // 자료는 [자료 준비]의 것을 그대로 쓴다 — 창을 열 때마다 새로 읽는다
        EvalPlan = new EvalPlanTabViewModel(
            evalPlan,
            () => _workspace.Plans,
            () => _scales.Active.Levels.Select(l => l.Label).ToList(),
            Log, _progress, () => IsConnected);

        if (_profiles.IsSubjectMode)
            InitSubjectAxis();        // 전담: 등록된 반 목록·첫 조합 로드
        else
            RestoreLastFiles();       // 담임: 최근 사용 자료 자동 로드 (없으면 조용히 넘어감)

        // 연결·상태는 NeisSessionController 전담 (R9) — 여기선 로그·바인딩 중계만
        _session.Log += Log;
        _session.PropertyChanged += (_, e) =>
        {
            OnPropertyChanged(e.PropertyName);
            if (e.PropertyName is nameof(IsConnected) or nameof(ConnectionHint))
            {
                OnPropertyChanged(nameof(ShowConnectionHint));
                if (e.PropertyName == nameof(IsConnected))
                {
                    RefreshNextStep();
                    Timetable.RefreshRunnable();   // 연결돼야 시간표 입력을 시작할 수 있다
                    EvalPlan.RefreshRunnable();    // 평가계획도 마찬가지
                }
                else OnPropertyChanged(nameof(ShowNextStep));   // 연결 배너와 상호배타
            }
        };
        _session.Start();
    }

    private readonly NeisSessionController _session;
    private readonly TimetableSession _timetable;

    // ── 최근 파일 · 자동 로드 ──────────────────

    public ICommand OpenRecentCommand { get; }

    /// <summary>최근 파일 메뉴 항목 (실존 파일만, 평가계획서·성적파일 구분).</summary>
    public IReadOnlyList<(string Path, string Display, bool IsPlan)> RecentEntries => _workspace.RecentEntries;

    /// <summary>시작 시 마지막으로 쓰던 성적파일·평가계획서를 복원. 실패는 로그만 (팝업 없음).
    /// 성적을 먼저 열어야 평가계획 로드가 성적표를 새로 만들지 않는다.</summary>
    private void RestoreLastFiles()
    {
        if (_workspace.LastGradePath is { } grades) LoadGrades(grades, silent: true);
        if (_workspace.LastPlanPath is { } plan) LoadPlan(plan, silent: true);
    }

    // ── 연결·상태 — NeisSessionController 위임 (R9). XAML 바인딩 경로 유지용 ──

    /// <summary>나이스 연결 여부 — 입력 버튼 활성/[NEIS 접속] 버튼 표시 제어 (U3·U5).</summary>
    public bool IsConnected => _session.IsConnected;

    /// <summary>미연결 시 다음에 뭘 해야 하는지 안내 (실패 원인별). 연결되면 빈 문자열.</summary>
    public string ConnectionHint => _session.ConnectionHint;

    /// <summary>연결 안내 배너 표시 여부 — 미연결이고 안내 문구가 있을 때.</summary>
    public bool ShowConnectionHint => !IsConnected && !string.IsNullOrEmpty(ConnectionHint);

    // ── 진행 안내 (U1): 자료 로드 후 '다음 할 일'을 짚어준다 ──
    private bool _nextStepDismissed;
    /// <summary>이번 실행에서 사용자가 진행 안내를 닫았는지.</summary>
    public bool NextStepDismissed
    {
        get => _nextStepDismissed;
        set { if (SetProperty(ref _nextStepDismissed, value)) OnPropertyChanged(nameof(ShowNextStep)); }
    }

    /// <summary>다음 할 일 안내 문구. 자료 없음(빈 화면 카드가 담당)·완료 상태면 빈 문자열.</summary>
    public string NextStep
    {
        get
        {
            if (Subjects.Count == 0) return "";   // 자료 없음 → 빈 화면 카드가 안내
            bool anyGrade = Subjects.Any(s => s.Snapshot().Students.Any(st => st.Grades.Count > 0));
            if (!anyGrade)
                return "다음: 성적표에서 등급을 입력하세요. 셀을 여러 개 선택하고 숫자키(1·2·3…)나 드래그로 한 번에 지정할 수 있어요.";
            if (!IsConnected)
                return "다음: 나이스에 입력하려면 [🌐 NEIS 접속]으로 로그인·조회해 연결하세요. 서술문은 [교과학습] 탭에서 만듭니다.";
            return "준비 완료! 과목 탭에서 [▶ 이 과목 입력] 또는 [🚀 전과목 입력]으로 나이스에 넣으세요. 서술문은 [교과학습] 탭에서.";
        }
    }

    /// <summary>진행 안내 표시 여부 — 자료가 있고, 안 닫았고, 안내 문구가 있고, 연결 안내 배너와 겹치지 않을 때.</summary>
    public bool ShowNextStep =>
        !NextStepDismissed && !ShowConnectionHint && Subjects.Count > 0 && NextStep.Length > 0;

    /// <summary>진행 안내 닫기.</summary>
    public ICommand DismissNextStepCommand => _dismissNextStep ??= new RelayCommand(() => NextStepDismissed = true);
    private ICommand? _dismissNextStep;

    /// <summary>상태(자료·성적·연결)가 바뀌면 진행 안내를 다시 계산.</summary>
    public void RefreshNextStep()
    {
        OnPropertyChanged(nameof(NextStep));
        OnPropertyChanged(nameof(ShowNextStep));
    }


    // ── 평가계획서 — 상태·파일 IO 는 WorkspaceService 전담 ──
    public IReadOnlyList<SubjectPlan> Plans => _workspace.Plans;

    // ── 명단·평가계획 인앱 편집 ──────────────
    public ICommand OpenPlanEditorCommand { get; }

    /// <summary>평가계획 문서를 나이스에 넣는 창을 연다 — 평가 탭 안에 둔다.</summary>


    /// <summary>
    /// 평가계획을 준비하러 간다 — 담임은 <b>평가계획 탭</b>, 전담은 반·학년까지 다뤄야 해 <b>자료 준비 창</b>.
    /// </summary>
    public ICommand PreparePlansCommand => _preparePlans ??= new RelayCommand(() =>
    {
        if (_profiles.IsSubjectMode) { OpenPlanEditor(); return; }

        PreparePlanEditor();
        PlanTabRequested?.Invoke();
    });
    private ICommand? _preparePlans;

    /// <summary>
    /// 평가계획 탭을 앞으로 불러 달라는 신호 — 창이 받아서 탭을 옮긴다.
    /// (탭 조작은 화면의 일이라 뷰모델이 직접 하지 않는다.)
    /// </summary>
    public event Action? PlanTabRequested;

    /// <summary>
    /// 드래그앤드롭된 평가계획 문서(pdf/hwp/hwpx) → AI 가져오기 시작.
    /// 담임은 <b>평가계획 탭</b>에서, 전담은 반·학년을 함께 고쳐야 해 <b>자료 준비 창</b>에서 받는다.
    /// </summary>
    public void ImportPlanDocument(string path)
    {
        if (_profiles.IsSubjectMode) { OpenPlanEditor(path); return; }

        PreparePlanEditor();
        PlanTabRequested?.Invoke();
        _ = _planEditorVm?.ImportPlanFileAsync(path);
    }

    // 담임 import: 과목 목록 인식 → 선택 콜백 → 고른 과목만 (F9 M4b)
    private Task<IReadOnlyList<SubjectPlan>> ImportPlansAsync(string path, IProgress<string> progress,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>? select) =>
        new Generator.GasPlanImporter(AppHttp.Long, _generatorSettings.Options)
            .ImportAsync(path, _scales.Active, progress, select);

    // 전담 import: (학년·과목) 단위 인식 → 선택 콜백 → 학년별 세트 (F9 M4b)
    private Task<IReadOnlyList<Generator.GasPlanImporter.GradePlanSet>> ImportPlanUnitsAsync(
        string path, IProgress<string> progress,
        Func<IReadOnlyList<NeisAutoFill.Core.PlanUnit>, Task<IReadOnlyList<NeisAutoFill.Core.PlanUnit>?>>? select) =>
        new Generator.GasPlanImporter(AppHttp.Long, _generatorSettings.Options)
            .ImportUnitsAsync(path, _scales.Active, progress, select);

    /// <summary>
    /// 편집표 속을 하나 만든다 — [평가계획] 탭과 [자료 준비] 창이 같은 것을 쓴다.
    /// 메인에서 보던 과목·반이 기본으로 잡힌다.
    /// </summary>
    private PlanEditorViewModel NewPlanEditor()
    {
        // 전담: 반별 명단·학년별 계획을 직접 저장(SubjectModeStore) — 담임 워크스페이스 저장 안 탐
        if (_profiles.IsSubjectMode)
            return new PlanEditorViewModel(Array.Empty<SubjectPlan>(), Array.Empty<(string, string)>(),
                _scales.Active, ImportPlansAsync, new SubjectModeStore(_scales.Active),
                SelectedSubject?.OwnerClass, ImportPlanUnitsAsync, _currentSubject);

        // 담임: 명단 없으면 열린 성적파일 명단 재사용
        var roster = _workspace.Roster;
        if (roster.Count == 0 && Subjects.Count > 0)
            roster = Subjects[0].Snapshot().Students.Select(s => (s.No, s.Name)).ToList();

        return new PlanEditorViewModel(_workspace.Plans, roster, _scales.Active, ImportPlansAsync,
            initialSubject: SelectedSubject?.SubjectName);
    }

    /// <summary>
    /// <b>전담 전용</b> 자료 준비 창 — 반을 등록하고 반별 명단을 넣는다.
    ///
    /// 담임에게는 이 창이 없다. 계획은 [평가계획] 탭에서, 명단은 [교과평가] 표에서 바로 고친다
    /// (사용자 요청 2026-08-22). 전담은 반 자체를 만들고 반마다 명단이 달라 아직 이 창이 필요하다.
    /// </summary>
    private void OpenPlanEditor(string? importPath = null)
    {
        var vm = NewPlanEditor();
        var win = new PlanEditorWindow(vm) { Owner = Application.Current.MainWindow };
        if (importPath is not null) win.Loaded += async (_, _) => await vm.ImportPlanFileAsync(importPath);
        win.ShowDialog();
        vm.SaveSubjectMode();   // 현재 반·학년 저장 (전환 시마다 이미 저장되지만 마지막 것도)
        Log("전담 명단·평가계획 저장 완료");
    }

    private string _planName = "평가계획서 없음";
    public string PlanName { get => _planName; set => SetProperty(ref _planName, value); }

    private void LoadPlan(string path, bool silent = false)
    {
        var error = _workspace.LoadPlan(path);
        if (error is not null)
        {
            if (silent) Log($"⚠ 최근 평가계획서를 다시 열지 못했습니다: {error}");
            else ShowError($"평가계획서 오류: {error}");
            return;
        }
        PlanName = Path.GetFileName(path);
        OnPropertyChanged(nameof(RecentEntries));
        RefreshCriteriaPanel();
        Log($"평가계획서 로드: {PlanName} " +
            $"({string.Join(", ", _workspace.Plans.Select(p => $"{p.SubjectName} {p.Domains.Count}영역"))}" +
            (_workspace.Roster.Count > 0 ? $" / 명단 {_workspace.Roster.Count}명)" : ")"));
        SyncGradeTableWithPlan();   // 성적표 없으면 생성, 있으면 명단·영역 변경 동기화 (성적 보존)
    }

    public ObservableCollection<SubjectViewModel> Subjects { get; } = new();

    // ── 전담: 메인 과목 축 (F9 M7) — 과목=콤보+[이동], 반=탭 ──
    // 전담은 적은 과목을 여러 반에 걸쳐 입력하므로, 자주 바꾸는 반이 탭·과목이 선택기가 된다.
    /// <summary>전담 모드인가 — 상단 과목 콤보 표시.</summary>
    public bool IsSubjectMode => _profiles.IsSubjectMode;

    private IReadOnlyList<NeisAutoFill.Core.ClassRef> _allClasses = Array.Empty<NeisAutoFill.Core.ClassRef>();

    /// <summary>등록된 전 과목 목록 (과목 콤보).</summary>
    public ObservableCollection<string> SubjectChoices { get; } = new();

    private string? _pickSubject;
    /// <summary>과목 콤보 선택값 — [이동]을 눌러야 그 과목의 반 탭들로 전환된다.</summary>
    public string? PickSubject { get => _pickSubject; set => SetProperty(ref _pickSubject, value); }

    /// <summary>[이동] — 선택한 과목을 가르치는 반들을 탭으로 띄운다.</summary>
    public ICommand GoSubjectCommand => _goSubject ??= new RelayCommand(GoSubject,
        () => !string.IsNullOrEmpty(PickSubject));
    private ICommand? _goSubject;

    private string? _currentSubject;   // 현재 탭들이 보고 있는 과목

    private static int ClassNum(string cls) => int.TryParse(new string(cls.Where(char.IsDigit).ToArray()), out var n) ? n : 0;

    /// <summary>과목의 (반,과목) 성적 파일 경로.</summary>
    private static string UnitPath(string wsRoot, NeisAutoFill.Core.ClassRef c, string subject) =>
        NeisAutoFill.Core.SubjectModePaths.UnitGradeFile(wsRoot, new NeisAutoFill.Core.TeachingUnit(c.Grade, c.Class, subject));

    /// <summary>[이동] — 선택 과목을 가르치는 반(그 학년 계획에 이 과목이 있는 반)들을 탭으로 구성.</summary>
    private void GoSubject()
    {
        if (string.IsNullOrEmpty(PickSubject)) return;
        SaveSubjectTabsIfDirty();   // 보던 과목의 반 탭들 저장
        _currentSubject = PickSubject;

        var store = new SubjectModeStore(_scales.Active);
        var wsRoot = AppPaths.EnsureWorkspaceRoot();
        var planByGrade = new Dictionary<int, IReadOnlyList<SubjectPlan>>();

        var tabs = new List<(SubjectSheet Sheet, NeisAutoFill.Core.ClassRef Owner)>();
        foreach (var c in _allClasses.OrderBy(c => c.Grade).ThenBy(c => ClassNum(c.Class)))
        {
            if (!planByGrade.TryGetValue(c.Grade, out var plans))
                planByGrade[c.Grade] = plans = store.LoadPlan(c.Grade);
            var plan = plans.FirstOrDefault(p => p.SubjectName == _currentSubject);
            if (plan is null) continue;   // 이 반 학년엔 이 과목이 없음 → 탭 제외

            var roster = store.LoadRoster(c);
            var path = UnitPath(wsRoot, c, _currentSubject);
            SubjectSheet? old = null;
            if (File.Exists(path))
                try { old = NeisAutoFill.Excel.WorkbookLoader.Load(path).FirstOrDefault(); }
                catch { /* 손상 시 새로 */ }
            tabs.Add((NeisAutoFill.Core.SheetSynchronizer.BuildUnitSheet(plan, roster, old), c));
        }

        if (tabs.Count == 0)
        {
            Subjects.Clear();
            Log($"'{_currentSubject}' 을(를) 가르치는 반이 없습니다 — 자료 준비에서 그 학년 계획에 이 과목을 넣으세요.");
            return;
        }

        ReplaceClassTabs(tabs);
        Log($"전담 {_currentSubject} — {tabs.Count}개 반 ({string.Join(", ", tabs.Select(t => t.Owner.Key))})");
        // 나이스 이동은 여기서 안 함 — 실제 [이 반 입력]할 때 그 반·과목으로 이동한다.
    }

    /// <summary>전담 서술문 창용 컨텍스트 — 창 안에서 교과·학년반을 독립적으로 골라 로드 (F9 M11).
    /// 데이터 조립·narratives 전환을 메인이 맡아, 서술문 창은 선택만 하면 된다. null 이면 담임.</summary>
    private GeneratorViewModel.SubjectModeGen? BuildSubjectModeGen()
    {
        if (!IsSubjectMode) return null;
        var store = new SubjectModeStore(_scales.Active);
        var wsRoot = AppPaths.EnsureWorkspaceRoot();

        // 과목 → 그 과목을 가르치는 반 목록 (그 학년 계획에 과목이 있는 반만)
        IReadOnlyList<NeisAutoFill.Core.ClassRef> ClassesOf(string subject) =>
            _allClasses.OrderBy(c => c.Grade).ThenBy(c => ClassNum(c.Class))
                .Where(c => store.LoadPlan(c.Grade).Any(p => p.SubjectName == subject))
                .ToList();

        // (학년,반,과목) → 성적표 + 계획. narratives 도 그 반으로 전환한다.
        (SubjectSheet Sheet, IReadOnlyList<SubjectPlan> Plans)? Load(int grade, string cls, string subject)
        {
            var plan = store.LoadPlan(grade).FirstOrDefault(p => p.SubjectName == subject);
            if (plan is null) return null;
            var c = new NeisAutoFill.Core.ClassRef(grade, cls);
            var roster = store.LoadRoster(c);
            var path = UnitPath(wsRoot, c, subject);
            SubjectSheet? old = null;
            if (File.Exists(path))
                try { old = NeisAutoFill.Excel.WorkbookLoader.Load(path).FirstOrDefault(); }
                catch { /* 손상 시 새로 */ }
            var narrPath = NeisAutoFill.Core.ProfilePaths.DataFile(AppPaths.Root, c.Key, "narratives.json");
            _narratives.SwitchTo(narrPath);
            return (NeisAutoFill.Core.SheetSynchronizer.BuildUnitSheet(plan, roster, old),
                    store.LoadPlan(grade).Where(p => p.SubjectName == subject).ToList());
        }

        return new GeneratorViewModel.SubjectModeGen(
            SubjectChoices.ToList(),
            s => ClassesOf(s).Select(c => (c.Grade, c.Class)).ToList(),
            Load,
            // 창 열 때 메인에서 보던 과목·반이 기본 선택되도록
            _currentSubject,
            SelectedSubject?.OwnerClass is { } oc ? (oc.Grade, oc.Class) : null);
    }

    /// <summary>전담: 반 탭들을 SubjectViewModel 로 만들어 표시 (탭 머리글=반, 각 탭에 소속 반 기록).</summary>
    private void ReplaceClassTabs(IReadOnlyList<(SubjectSheet Sheet, NeisAutoFill.Core.ClassRef Owner)> tabs)
    {
        Subjects.Clear();
        foreach (var (sheet, owner) in tabs)
            Subjects.Add(new SubjectViewModel(this, sheet, tabLabel: owner.Key, ownerClass: owner));
        SelectedSubject = Subjects.FirstOrDefault();
        NextStepDismissed = false;
        RefreshNextStep();
        OnPropertyChanged(nameof(RecentEntries));
    }

    /// <summary>전담: 편집된 반 탭들을 각자의 (반,과목) 성적 파일에 저장.</summary>
    private void SaveSubjectTabsIfDirty()
    {
        if (!IsSubjectMode || _currentSubject is null) return;
        var wsRoot = AppPaths.EnsureWorkspaceRoot();
        foreach (var vm in Subjects.Where(s => s.IsDirty && s.OwnerClass is not null))
        {
            var path = UnitPath(wsRoot, vm.OwnerClass!.Value, _currentSubject);
            var err = _workspace.SaveGrades(new[] { vm.Snapshot() }, path);
            if (err is null) vm.MarkSaved();
        }
    }

    /// <summary>탭(반)이 바뀌면 그 반의 서술문 파일로 전환한다. (나이스 이동은 탭 훑기가 아니라
    /// 실제 [이 반 입력]할 때만 — 매 탭 클릭마다 나이스가 움직이면 안 되므로.)</summary>
    private void OnSubjectTabChanged(SubjectViewModel? tab)
    {
        if (!IsSubjectMode || tab?.OwnerClass is not { } c) return;
        var narrPath = NeisAutoFill.Core.ProfilePaths.DataFile(AppPaths.Root, c.Key, "narratives.json");
        _narratives.SwitchTo(narrPath);
    }

    /// <summary>전담 모드 초기화 — 등록된 전 과목을 채우고 첫 과목을 로드 (앱 시작 시).</summary>
    private void InitSubjectAxis()
    {
        if (!IsSubjectMode) return;
        OnPropertyChanged(nameof(RunOneLabel));
        OnPropertyChanged(nameof(RunAllLabel));
        var store = new SubjectModeStore(_scales.Active);
        _allClasses = store.ListClasses();

        // 등록된 모든 학년 계획에서 과목명을 모은다 (등장 순서 보존)
        foreach (var g in _allClasses.Select(c => c.Grade).Distinct().OrderBy(g => g))
            foreach (var p in store.LoadPlan(g))
                if (!SubjectChoices.Contains(p.SubjectName)) SubjectChoices.Add(p.SubjectName);

        _pickSubject = SubjectChoices.FirstOrDefault();
        OnPropertyChanged(nameof(PickSubject));
        (_goSubject as RelayCommand)?.RaiseCanExecuteChanged();
        if (_pickSubject is not null) GoSubject();   // 첫 과목 자동 로드 (시작 시엔 편함)
    }

    private SubjectViewModel? _selectedSubject;
    public SubjectViewModel? SelectedSubject
    {
        get => _selectedSubject;
        set
        {
            if (!SetProperty(ref _selectedSubject, value)) return;
            RefreshCriteriaPanel();
            OnSubjectTabChanged(value);   // 전담: 반 탭 전환 시 서술문·나이스 이동
        }
    }

    /// <summary>시간표 탭 — 문서 읽기부터 나이스 입력까지 그 탭 안에서 끝낸다.</summary>
    public TimetableTabViewModel Timetable { get; }

    /// <summary>평가계획 입력 — 평가 탭 안 [평가계획] 갈래가 쓴다.</summary>
    public EvalPlanTabViewModel EvalPlan { get; }

    // ── 본문 탭 (0 = 평가, 1 = 시간표) ──────────
    private int _mainTabIndex;
    public int MainTabIndex
    {
        get => _mainTabIndex;
        set
        {
            if (!SetProperty(ref _mainTabIndex, value)) return;
            OnPropertyChanged(nameof(ShowCriteriaColumn));
        }
    }

    // ── 성취기준 참조 패널 (토글) ──────────────
    private bool _showCriteriaPanel;
    public bool ShowCriteriaPanel
    {
        get => _showCriteriaPanel;
        set
        {
            if (!SetProperty(ref _showCriteriaPanel, value)) return;
            _appState.State.ShowCriteriaPanel = value;
            _appState.Save();
            OnPropertyChanged(nameof(ShowCriteriaColumn));
        }
    }

    /// <summary>
    /// 성취기준 패널을 지금 보여 줄지.
    /// 평가와 상관없는 시간표 탭에서는 켜져 있어도 감춘다 — 화면을 좁힐 이유가 없다.
    /// </summary>
    public bool ShowCriteriaColumn => _showCriteriaPanel && _mainTabIndex == 0;

    private IReadOnlyList<CriteriaPanelBuilder.DomainView> _criteriaPanelItems =
        Array.Empty<CriteriaPanelBuilder.DomainView>();
    public IReadOnlyList<CriteriaPanelBuilder.DomainView> CriteriaPanelItems
    {
        get => _criteriaPanelItems;
        private set => SetProperty(ref _criteriaPanelItems, value);
    }

    private string _criteriaPanelStatus = "";
    public string CriteriaPanelStatus { get => _criteriaPanelStatus; set => SetProperty(ref _criteriaPanelStatus, value); }

    /// <summary>현재 과목 탭의 평가계획(영역·등급별 기준)을 패널용으로 재구성 (구성 로직은 Core/CriteriaPanelBuilder).</summary>
    private void RefreshCriteriaPanel()
    {
        var subjectName = SelectedSubject?.SubjectName;
        // 전담: 계획은 SubjectModeStore(그 반의 학년)에 있다. 담임: workspace 계획.
        SubjectPlan? plan;
        if (IsSubjectMode && SelectedSubject?.OwnerClass is { } oc && subjectName is not null)
            plan = new SubjectModeStore(_scales.Active).LoadPlan(oc.Grade)
                       .FirstOrDefault(p => p.SubjectName == subjectName);
        else
            plan = subjectName is null ? null : _workspace.Plans.FirstOrDefault(p => p.SubjectName == subjectName);
        if (plan is null)
        {
            CriteriaPanelItems = Array.Empty<CriteriaPanelBuilder.DomainView>();
            CriteriaPanelStatus = subjectName is null
                ? "성적파일을 불러오면 표시됩니다."
                : $"'{subjectName}' 평가계획이 없습니다.\n[📁 자료 준비]에서 입력하거나 평가계획서를 불러오세요.";
            return;
        }

        CriteriaPanelItems = CriteriaPanelBuilder.Build(plan, _scales.Active);
        CriteriaPanelStatus = "";
    }

    /// <summary>현재 활성 척도 요약 (예: "잘함/보통/노력요함").</summary>
    public string ActiveScaleSummary =>
        string.Join("/", _scales.Active.Levels.Select(l => l.Label));

    /// <summary>성적 표 드롭다운 편집용 등급 라벨 (빈칸 선택 허용).</summary>
    public IReadOnlyList<string> GradeLabels =>
        new[] { "" }.Concat(_scales.Active.Levels.Select(l => l.Label)).ToList();

    /// <summary>일괄 입력 버튼용 등급 라벨 (빈칸 제외).</summary>
    public IReadOnlyList<string> BulkGradeLabels =>
        _scales.Active.Levels.Select(l => l.Label).ToList();

    // ── 지역 선택 (시도 교육청 나이스 주소) ──
    public IReadOnlyList<NeisRegion> Regions => NeisRegions.All;

    private NeisRegion _selectedRegion;
    public NeisRegion SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (SetProperty(ref _selectedRegion, value))
            {
                _engineOptions.NeisUrl = value.Url;
                _generatorSettings.Options = _generatorSettings.Options with { NeisRegionCode = value.Code };
                _generatorSettings.Save();
                Log($"나이스 지역: {value.Name} ({value.Url})");
            }
        }
    }

    public ICommand OpenScaleEditorCommand { get; }

    private void OpenScaleEditor()
    {
        var win = new SettingsWindow(new SettingsViewModel(_scales, _generatorSettings, _profiles))
        {
            Owner = Application.Current.MainWindow,
        };
        if (win.ShowDialog() != true) return;

        // 지역·척도가 바뀌었을 수 있으므로 전부 재반영
        _selectedRegion = NeisRegions.Find(_generatorSettings.Options.NeisRegionCode);
        _engineOptions.NeisUrl = _selectedRegion.Url;
        OnPropertyChanged(nameof(SelectedRegion));
        OnPropertyChanged(nameof(ActiveScaleSummary));
        OnPropertyChanged(nameof(GradeLabels));
        OnPropertyChanged(nameof(BulkGradeLabels));
        RefreshCriteriaPanel();
        Log($"설정 적용: 척도 {ActiveScaleSummary} · 지역 {_selectedRegion.Name}");
    }

    public string VersionText => "v" + (System.Reflection.Assembly.GetExecutingAssembly()
        .GetName().Version?.ToString(3) ?? "1.0");

    public string ConnectionText => _session.ConnectionText;      // 상태칩 글자 (R9 위임)
    public Brush ConnectionBrush => _session.ConnectionBrush;     // 상태칩 색 (R9 위임)

    private string _excelName = "성적파일 없음";
    public string ExcelName { get => _excelName; set => SetProperty(ref _excelName, value); }

    /// <summary>백그라운드 서술문 생성 상태 — 메인 하단 상태줄에 상시 표시.</summary>
    public string GenerationStatus => _generationQueue.Status;

    private double _progressValue;
    public double ProgressValue { get => _progressValue; set => SetProperty(ref _progressValue, value); }

    private double _progressMax = 1;
    public double ProgressMax { get => _progressMax; set => SetProperty(ref _progressMax, value); }

    private readonly StringBuilder _log = new();
    private string _logText = "";
    public string LogText { get => _logText; set => SetProperty(ref _logText, value); }

    public ICommand LaunchEdgeCommand { get; }
    public ICommand OpenExcelCommand { get; }


    // ── 평가계획 탭 (계획 편집표 ↔ 나이스 올리기) ──────────────

    private PlanEditorViewModel? _planEditorVm;

    /// <summary>평가계획 편집표의 속. 그 탭을 처음 열 때 만들어진다.</summary>
    public PlanEditorViewModel? PlanEditor => _planEditorVm;

    private bool _planTabShowsUpload;

    /// <summary>평가계획 탭이 지금 <b>올리기 화면</b>을 보이고 있나 (아니면 편집표).</summary>
    public bool PlanTabShowsUpload
    {
        get => _planTabShowsUpload;
        private set => SetProperty(ref _planTabShowsUpload, value);
    }

    /// <summary>
    /// 평가계획 탭을 열 때 부른다. 편집표 속을 만들고, 항상 <b>편집표부터</b> 보여 준다.
    /// </summary>
    public void PreparePlanEditor()
    {
        PlanTabShowsUpload = false;

        if (_planEditorVm is not null) return;

        try
        {
            _planEditorVm = NewPlanEditor();

            // 문서를 읽어 오면 곧바로 저장·반영한다 — 사용자가 단추를 한 번 더 누를 이유가 없다
            _planEditorVm.Imported += () => ApplyPlanEdits();

            OnPropertyChanged(nameof(PlanEditor));
        }
        catch (Exception ex)
        {
            Log($"평가계획 편집표 준비 오류: {ex.Message}");
            ShowError(ex.ToString());
        }
    }

    /// <summary>
    /// [나이스에 올리기]. <b>먼저 저장해서 반영한 뒤</b> 올리기 화면으로 넘어간다 —
    /// 여기서 고친 내용이 그대로 나이스에 들어가야 하기 때문이다(사용자 요청 2026-08-22).
    /// </summary>
    public ICommand ShowPlanUploadCommand => _showPlanUpload ??= new RelayCommand(() =>
    {
        if (!ApplyPlanEdits()) return;

        EvalPlan.Refresh();
        PlanTabShowsUpload = true;
    });
    private ICommand? _showPlanUpload;

    /// <summary>[← 계획 편집] — 올리기 화면에서 편집표로 되돌아간다.</summary>
    public ICommand ShowPlanEditCommand => _showPlanEdit ??=
        new RelayCommand(() => PlanTabShowsUpload = false);
    private ICommand? _showPlanEdit;

    public ICommand SavePlanEditsCommand => _savePlanEdits ??= new RelayCommand(() => ApplyPlanEdits());
    private ICommand? _savePlanEdits;

    /// <summary>
    /// 편집표의 내용을 작업공간 파일에 저장하고 프로그램 전체에 반영한다.
    ///
    /// <b>명단은 편집표가 아니라 [교과평가] 표에서 가져온다.</b> 명단을 고치는 곳은 이제 그 표 하나뿐이라,
    /// 편집표가 들고 있는 (열 때 떠 온) 옛 명단으로 덮어쓰면 방금 고친 명단이 날아간다.
    /// </summary>
    public bool ApplyPlanEdits()
    {
        if (_planEditorVm is null) return false;

        if (_profiles.IsSubjectMode)
        {
            _planEditorVm.SaveSubjectMode();
            Log("전담 평가계획 저장 완료");
            return true;
        }

        var built = _planEditorVm.Build(out var error);
        if (built is null) { ShowError(error ?? "편집 내용을 읽지 못했습니다."); return false; }

        var roster = Subjects.Count > 0
            ? Subjects[0].Snapshot().Students.Select(s => (s.No, s.Name)).ToList()
            : built.Value.Roster.ToList();

        var (savedPath, saveError) = _workspace.SavePlan(built.Value.Plans, roster);
        if (saveError is not null)
        {
            ShowError($"평가계획 저장 실패: {saveError}\n(파일이 엑셀에서 열려 있으면 닫고 다시 시도하세요)");
            return false;
        }

        Log($"평가계획 저장: {Path.GetFileName(savedPath!)}");
        LoadPlan(savedPath!);   // 저장본을 다시 읽어 반영 (엑셀 직접 수정과 같은 경로)
        return true;
    }

    /// <summary>
    /// 교과학습(세특) 화면의 속. 처음 <b>그 탭을 열 때</b> 만들어진다 —
    /// 만드는 값이 비싸고(과목·계획 스냅샷), 안 쓰는 사람도 있기 때문이다.
    /// </summary>
    public GeneratorViewModel? Generator => _generatorVm;

    /// <summary>
    /// 교과학습 탭을 열 때 부른다. 전에는 창을 여는 일이었다(사용자 요청 2026-08-22 로 탭이 됨).
    /// 메인에서 지금 보고 있는 과목·반에 맞춰 놓는 것이 핵심이다.
    /// </summary>
    public void PrepareGenerator()
    {
        try
        {
            var isNew = _generatorVm is null;

            _generatorVm ??= new GeneratorViewModel(
                // 담임: 메인의 전 과목 성적·계획. 전담이면 아래 subjectMode 가 자체 구동하므로 안 쓰임.
                () => Subjects.Select(s => s.Snapshot()).ToList(),
                () => _workspace.Plans,
                _scales, _generatorSettings, _narratives, _generationQueue, _narrativeMirror, _engine, _session, Log,
                // 전담: 창 안에서 교과·학년반을 골라 세특 생성·입력 (메인에서 보던 조합이 기본 선택)
                subjectMode: BuildSubjectModeGen());
            if (_generatorVm.IsSubjectMode)
                // 싱글턴이라 열 때마다 메인에서 지금 보는 (과목·반)으로 맞춘다
                _generatorVm.FocusUnit(_currentSubject,
                    SelectedSubject?.OwnerClass is { } oc ? (oc.Grade, oc.Class) : null);
            else
                _generatorVm.RefreshSubjects();   // 담임: 메인에서 로드된 성적·평가계획을 자동 반영

            if (isNew) OnPropertyChanged(nameof(Generator));
        }
        catch (Exception ex)
        {
            Log($"교과학습 화면 준비 오류: {ex.Message}");
            ShowError(ex.ToString());
        }
    }

    // 화면 진단(InspectDomAsync)·dry-run 은 UI 에서 제거됨.
    // 코드·복원 방법: docs/보관_진단_검증도구.md

    public void Log(string s)
    {
        _log.AppendLine(s);
        LogText = _log.ToString();

        // 상태줄 (U2): 마지막 로그 한 줄 + 문제(⚠/✗/오류) 시 색 변경으로 확인 유도
        LastLogLine = s;
        bool problem = s.Contains('⚠') || s.Contains('✗') || s.Contains("오류") || s.Contains("실패");
        LastLogBrush = new SolidColorBrush(problem ? Color.FromRgb(0xB4, 0x53, 0x09) : Color.FromRgb(0x64, 0x74, 0x8B));
    }

    private string _lastLogLine = "준비됨";
    public string LastLogLine { get => _lastLogLine; set => SetProperty(ref _lastLogLine, value); }

    private Brush _lastLogBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    public Brush LastLogBrush { get => _lastLogBrush; set => SetProperty(ref _lastLogBrush, value); }

    private bool _logExpanded;
    /// <summary>로그 전체 펼침 (기본 접힘 — 상태줄만). state.json 에 유지.</summary>
    public bool LogExpanded
    {
        get => _logExpanded;
        set
        {
            if (!SetProperty(ref _logExpanded, value)) return;
            _appState.State.LogExpanded = value;
            _appState.Save();
        }
    }

    private void LaunchEdge()
    {
        try
        {
            _engine.LaunchEdge();
            Log("Edge 실행됨. 로그인 후 [교과별 평가]에서 과목을 조회하세요. (연결은 자동으로 됩니다)");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenExcel()
    {
        var dlg = new OpenFileDialog { Filter = "Excel|*.xlsx;*.xlsm|모든 파일|*.*", Title = "성적파일 선택" };
        if (dlg.ShowDialog() == true) LoadGrades(dlg.FileName);
    }

    /// <summary>드래그앤드롭 진입점 — 평가계획서([학생명단] 시트 보유)인지 성적파일인지 자동 판별.</summary>
    public void LoadExcel(string path)
    {
        try
        {
            if (PlanWorkbookLoader.LooksLikePlan(path)) { LoadPlan(path); return; }
        }
        catch (Exception ex) { ShowError(ex.Message); return; }
        LoadGrades(path);
    }

    private void LoadGrades(string path, bool silent = false)
    {
        if (!ConfirmSaveIfDirty()) return;   // 기존 편집 보호
        var (sheets, error) = _workspace.LoadGrades(path);
        if (sheets is null)
        {
            if (silent) Log($"⚠ 최근 성적파일을 다시 열지 못했습니다: {error}");
            else ShowError(error!);
            return;
        }
        ReplaceSubjects(sheets, keepSelected: false);
        Log($"성적파일 로드: {ExcelName} ({string.Join(", ", sheets.Select(s => s.SubjectName))})");
    }

    /// <summary>Subjects 컬렉션을 새 시트로 교체하고 파일명·최근 목록 표시를 갱신.</summary>
    private void ReplaceSubjects(IReadOnlyList<SubjectSheet> sheets, bool keepSelected)
    {
        var selected = keepSelected ? SelectedSubject?.SubjectName : null;
        Subjects.Clear();
        foreach (var s in sheets) Subjects.Add(new SubjectViewModel(this, s));
        SelectedSubject = Subjects.FirstOrDefault(s => s.SubjectName == selected) ?? Subjects.FirstOrDefault();
        ExcelName = Path.GetFileName(_workspace.GradeFilePath ?? _workspace.DefaultGradePath);
        OnPropertyChanged(nameof(RecentEntries));
        NextStepDismissed = false;   // 자료가 바뀌면 안내를 다시 보여준다
        RefreshNextStep();
    }

    /// <summary>
    /// 평가계획·명단이 바뀌면 열려 있는 성적표를 그에 맞춘다 (계산은 WorkspaceService.ComputeSync).
    /// 성적표가 없으면 새로 만들고, 기존 학생의 성적·특기사항은 (번호,이름) 기준으로 보존.
    /// </summary>
    private void SyncGradeTableWithPlan()
    {
        if (Subjects.Count == 0) { EnsureGradeTableFromPlan(); return; }

        var synced = _workspace.ComputeSync(Subjects.Select(s => s.Snapshot()).ToList());
        if (synced is null) return;

        ReplaceSubjects(synced, keepSelected: true);
        var error = _workspace.SaveGrades(synced);
        if (error is null)
            Log($"성적표를 명단·평가계획에 맞춰 갱신: {ExcelName} (기존 성적은 번호·이름 기준 보존)");
        else
            Log($"⚠ 성적표 갱신 저장 실패 ({error}) — 편집·종료 때 다시 시도합니다.");
    }

    /// <summary>
    /// 성적표가 안 열려 있는데 평가계획+명단이 준비되면 성적표를 만들어 작업공간에 저장.
    /// 같은 파일이 이미 있으면 (지난 세션 성적 보호) 만들지 않고 그 파일을 연다.
    /// </summary>
    private void EnsureGradeTableFromPlan()
    {
        if (Subjects.Count > 0) return;

        if (File.Exists(_workspace.DefaultGradePath))
        {
            if (_workspace.BuildFreshSheets() is null) return;   // 재료 없으면 손대지 않음
            Log("작업공간에 기존 성적.xlsx 가 있어 그 파일을 엽니다. (새로 만들려면 파일을 옮기거나 지우세요)");
            LoadGrades(_workspace.DefaultGradePath, silent: true);
            if (Subjects.Count > 0) SyncGradeTableWithPlan();   // 파일이 현재 명단·계획과 다르면 맞춘다
            return;
        }

        var sheets = _workspace.BuildFreshSheets();
        if (sheets is null) return;

        var error = _workspace.SaveGrades(sheets, _workspace.DefaultGradePath);
        if (error is not null) { Log($"⚠ 성적표 파일 생성 실패: {error}"); return; }

        ReplaceSubjects(sheets, keepSelected: false);
        Log($"평가계획·명단으로 성적표 생성: {ExcelName} ({sheets.Count}과목 × {sheets[0].Students.Count}명) — 편집하면 자동 저장됩니다.");
    }

    // ── 자동 저장 ──────────────────────────────

    /// <summary>성적 표 편집 알림 (SubjectViewModel 에서 호출) — 디바운스 타이머 재시작.</summary>
    /// <summary>명단을 퍼뜨리는 중인가 — 되돌이 반영을 막는다.</summary>
    private bool _spreadingRoster;

    /// <summary>
    /// 명단 한 칸(번호·이름)이 바뀌면 <b>모든 과목 표에 같이</b> 반영한다.
    ///
    /// 명단은 과목마다 따로가 아니라 학급 하나의 것이다. 한 과목에서만 고치면
    /// 과목마다 다른 명단이 되어 나이스에 <b>엉뚱한 학생</b>이 들어간다.
    /// 줄 번호로 맞춘다 — 모든 과목 표가 같은 명단으로 같은 순서에 만들어진다.
    /// </summary>
    public void SpreadRoster(SubjectViewModel source, int rowIndex, string column, string value)
    {
        if (_spreadingRoster || rowIndex < 0) return;

        _spreadingRoster = true;
        try
        {
            foreach (var subject in Subjects)
            {
                if (ReferenceEquals(subject, source)) continue;
                if (rowIndex >= subject.Grid.Rows.Count) continue;

                var row = subject.Grid.Rows[rowIndex];
                if ((row[column]?.ToString() ?? "") != value) row[column] = value;
            }
        }
        finally { _spreadingRoster = false; }
    }

    // ── 명단 되돌리기 (Ctrl+Z) ──────────────────
    //
    // 칸 값 고치기는 과목마다 따로 되돌리지만(SubjectViewModel), 명단은 <b>모든 과목이 함께</b>
    // 늘고 줄어서 여기서 한 벌로 기억한다. 어느 쪽이 더 최근인지는 <b>한 곳에서 뽑은 번호표</b>로 가린다.
    private long _editSeq;

    /// <summary>되돌리기 차례를 정할 번호표를 하나 뽑는다.</summary>
    public long NextEditSeq() => ++_editSeq;

    /// <summary>되돌릴 명단 조작 하나. 더한 것이면 <c>Rows</c> 가 null, 뺀 것이면 그때 값이 들어 있다.</summary>
    /// <param name="Indexes">건드린 줄 번호 (오름차순)</param>
    /// <param name="Rows">[줄][과목] 순서로 떠 둔 값</param>
    private sealed record RosterUndo(
        long Seq, IReadOnlyList<int> Indexes, IReadOnlyList<IReadOnlyList<object?[]>>? Rows);

    private readonly Stack<RosterUndo> _rosterUndo = new();

    /// <summary>가장 최근 명단 조작의 번호표. 되돌릴 게 없으면 null.</summary>
    public long? RosterUndoSeq => _rosterUndo.Count == 0 ? null : _rosterUndo.Peek().Seq;

    /// <summary>
    /// 학생 한 명을 <b>모든 과목 표에</b> 더한다. 표 맨 아래 칸에서 Enter 를 치면 불린다.
    /// </summary>
    /// <returns>새로 생긴 줄 번호</returns>
    public int AddStudent() => AddStudents(1);

    /// <summary>학생 여러 명을 한꺼번에 더한다 — 되돌리기는 한 번에 묶인다(명단 붙여넣기).</summary>
    /// <returns>마지막으로 생긴 줄 번호. 과목이 없으면 -1.</returns>
    public int AddStudents(int count)
    {
        if (Subjects.Count == 0 || count <= 0) return -1;

        var first = Subjects[0].Grid.Rows.Count;

        _spreadingRoster = true;
        try
        {
            foreach (var subject in Subjects)
                for (int i = 0; i < count; i++)
                {
                    var row = subject.Grid.NewRow();
                    row["번호"] = (subject.Grid.Rows.Count + 1).ToString();
                    row["이름"] = "";
                    subject.Grid.Rows.Add(row);
                }
        }
        finally { _spreadingRoster = false; }

        _rosterUndo.Push(new(NextEditSeq(), Enumerable.Range(first, count).ToList(), null));
        NotifyGradesEdited();

        return Subjects[0].Grid.Rows.Count - 1;
    }

    /// <summary>학생 한 명을 <b>모든 과목 표에서</b> 뺀다.</summary>
    public void RemoveStudent(int rowIndex) => RemoveStudents(new[] { rowIndex });

    /// <summary>
    /// 학생 여러 명을 <b>모든 과목 표에서</b> 뺀다. 지우기 전에 값을 떠 두어 Ctrl+Z 로 그 자리에 돌아온다.
    /// </summary>
    /// <param name="undoable">false 면 되돌리기에 남기지 않는다 (이름 없이 생긴 임시 줄 취소).</param>
    /// <returns>실제로 뺀 학생 수</returns>
    public int RemoveStudents(IEnumerable<int> rowIndexes, bool undoable = true)
    {
        if (Subjects.Count == 0) return 0;

        var indexes = rowIndexes
            .Where(i => i >= 0 && i < Subjects[0].Grid.Rows.Count)
            .Distinct().OrderBy(i => i).ToList();
        if (indexes.Count == 0) return 0;

        var saved = indexes
            .Select(i => (IReadOnlyList<object?[]>)Subjects
                .Select(s => i < s.Grid.Rows.Count ? s.Grid.Rows[i].ItemArray : Array.Empty<object?>())
                .ToList())
            .ToList();

        _spreadingRoster = true;
        try
        {
            foreach (var subject in Subjects)
                foreach (var i in indexes.OrderByDescending(x => x))
                    if (i < subject.Grid.Rows.Count) subject.Grid.Rows.RemoveAt(i);
        }
        finally { _spreadingRoster = false; }

        if (undoable) _rosterUndo.Push(new(NextEditSeq(), indexes, saved));
        else DropTopAddIfMatches(indexes);

        NotifyGradesEdited();

        return indexes.Count;
    }

    /// <summary>방금 더한 줄을 도로 뺐다면 그 '더하기'도 되돌리기 목록에서 지운다 — 없던 일이 되어야 한다.</summary>
    private void DropTopAddIfMatches(IReadOnlyList<int> indexes)
    {
        if (_rosterUndo.Count == 0) return;

        var top = _rosterUndo.Peek();
        if (top.Rows is null && top.Indexes.SequenceEqual(indexes)) _rosterUndo.Pop();
    }

    /// <summary>가장 최근 명단 조작을 되돌린다. 되돌린 게 없으면 null, 있으면 사람이 읽을 설명.</summary>
    public string? UndoRoster()
    {
        if (_rosterUndo.Count == 0) return null;

        var op = _rosterUndo.Pop();

        _spreadingRoster = true;
        try
        {
            if (op.Rows is null)
            {
                foreach (var subject in Subjects)
                    foreach (var i in op.Indexes.OrderByDescending(x => x))
                        if (i < subject.Grid.Rows.Count) subject.Grid.Rows.RemoveAt(i);

                return $"학생 {op.Indexes.Count}명 추가 취소";
            }

            for (int k = 0; k < op.Indexes.Count; k++)
                for (int s = 0; s < Subjects.Count && s < op.Rows[k].Count; s++)
                {
                    var table = Subjects[s].Grid;
                    var row = table.NewRow();
                    if (op.Rows[k][s].Length == table.Columns.Count) row.ItemArray = op.Rows[k][s];
                    table.Rows.InsertAt(row, Math.Min(op.Indexes[k], table.Rows.Count));
                }

            return $"학생 {op.Indexes.Count}명 삭제 취소";
        }
        finally { _spreadingRoster = false; NotifyGradesEdited(); }
    }

    public void NotifyGradesEdited()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
        RefreshNextStep();   // 등급 입력이 시작되면 다음 안내가 바뀐다 (첫 등급 → 연결 유도)
    }

    /// <summary>편집이 잦아들면 저장 대상 파일에 조용히 저장. 실패(파일 잠금 등)는 로그만 남기고 dirty 유지.</summary>
    private void AutoSaveGrades()
    {
        if (!Subjects.Any(s => s.IsDirty)) return;

        // 전담: 반 탭마다 각자의 (반,과목) 성적 파일에 저장
        if (IsSubjectMode)
        {
            var before = Subjects.Count(s => s.IsDirty);
            SaveSubjectTabsIfDirty();
            var left = Subjects.Count(s => s.IsDirty);
            if (left == 0) Log($"자동 저장됨: {_currentSubject} {before}개 반");
            else Log($"⚠ 자동 저장 실패 — 성적 파일이 엑셀에서 열려 있으면 닫아 주세요. 다음 편집·종료 때 다시 시도합니다.");
            return;
        }

        var savePath = _workspace.GradeFilePath;
        if (savePath is null) return;
        var error = _workspace.SaveGrades(Subjects.Select(s => s.Snapshot()).ToList(), savePath);
        if (error is null)
        {
            foreach (var s in Subjects) s.MarkSaved();
            Log($"자동 저장됨: {Path.GetFileName(savePath)}");
        }
        else
            Log($"⚠ 자동 저장 실패 ({error}) — 파일이 엑셀에서 열려 있으면 닫아 주세요. 다음 편집·종료 때 다시 시도합니다.");
    }

    /// <summary>수정된 성적이 있으면 저장을 시도하고, 자동 저장이 불가능할 때만 묻는다. true=계속 진행, false=취소.</summary>
    public bool ConfirmSaveIfDirty()
    {
        if (!Subjects.Any(s => s.IsDirty)) return true;

        // 저장 대상 파일이 있으면 조용히 자동 저장 (평소 자동 저장과 같은 동작).
        // 전담은 반 탭마다 (반,과목) 파일로 저장되므로 workspace 경로가 없어도 자동 저장한다.
        if (IsSubjectMode || _workspace.GradeFilePath is not null)
        {
            AutoSaveGrades();
            if (!Subjects.Any(s => s.IsDirty)) return true;   // 저장 성공
        }

        var r = MessageBox.Show(
            "수정한 성적이 있는데 자동 저장하지 못했습니다. 엑셀 파일에 저장할까요?",
            "저장 확인", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) SaveGrades();
        else foreach (var s in Subjects) s.MarkSaved();   // 저장 안 함 → dirty 해제
        return true;
    }

    public ICommand HelpCommand { get; private set; } = null!;

    private ManualWindow? _manualWindow;   // 이미 열려 있으면 앞으로만 가져온다

    /// <summary>사용설명서 열기 — 내장 HTML 을 앱 스타일 팝업(WebView2)으로.
    /// WebView2 런타임이 없는 PC 는 기본 브라우저로 폴백. HelpUrl 이 지정되면 그 주소를 브라우저로.</summary>
    private async Task OpenHelpAsync()
    {
        var url = _generatorSettings.Options.HelpUrl?.Trim();
        if (!string.IsNullOrEmpty(url)) { OpenExternal(url); return; }

        var path = Path.Combine(AppContext.BaseDirectory, "사용설명서.html");
        if (!File.Exists(path))
        {
            ShowError("사용설명서 파일을 찾지 못했습니다. 프로그램을 다시 설치해 주세요.");
            return;
        }

        if (_manualWindow is not null)
        {
            _manualWindow.Activate();   // 이미 열림 → 앞으로
            return;
        }

        var win = new ManualWindow { Owner = Application.Current.MainWindow };
        win.Closed += (_, _) => _manualWindow = null;
        _manualWindow = win;
        win.Show();
        if (!await win.InitializeAsync(path))
        {
            win.Close();                // WebView2 런타임 없음 → 브라우저 폴백
            OpenExternal(path);
        }
    }

    private static void OpenExternal(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError($"사용설명서를 열지 못했습니다: {ex.Message}"); }
    }

    public ICommand ExportGradesCommand { get; private set; } = null!;

    /// <summary>현재 성적표 전체를 사용자가 고른 위치에 엑셀로 내보내기 (작업 파일과 별개 사본).</summary>
    private void ExportGrades()
    {
        if (Subjects.Count == 0)
        {
            ShowError("내보낼 성적이 없습니다. 성적을 먼저 준비해 주세요.");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "Excel|*.xlsx",
            FileName = $"성적_{DateTime.Now:yyyyMMdd}.xlsx",
            Title = "성적 엑셀 내보내기",
            InitialDirectory = AppPaths.EnsureWorkspace(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            GradeWorkbookWriter.Write(dlg.FileName, Subjects.Select(s => s.Snapshot()).ToList());
            Log($"성적 내보내기: {dlg.FileName}");
            MessageBox.Show($"내보냈습니다.\n{dlg.FileName}", "완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError($"내보내기 실패: {ex.Message}"); }
    }

    private void SaveGrades()
    {
        var path = _workspace.GradeFilePath;
        if (path is null)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "Excel|*.xlsx",
                FileName = "성적.xlsx",
                InitialDirectory = AppPaths.EnsureWorkspace(),
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        var error = _workspace.SaveGrades(Subjects.Select(s => s.Snapshot()).ToList(), path);
        if (error is not null) { ShowError($"저장 실패: {error}"); return; }
        ExcelName = Path.GetFileName(path);
        foreach (var s in Subjects) s.MarkSaved();
        Log($"성적 저장: {Path.GetFileName(path)}");
    }

    public async Task RunSubjectAsync(SubjectSheet sheet, bool dryRun,
        NeisAutoFill.Core.ClassRef? subjectModeClass = null)
    {
        var outcome = await RunSubjectCoreAsync(sheet, dryRun, subjectModeClass);
        if (outcome is null || dryRun) return;
        Helpers.UploadOutcomes.ShowSingle(outcome,
            retry: () => RunSubjectCoreAsync(sheet, dryRun: false, subjectModeClass));
    }

    /// <summary>단건 입력 본체 — 대시보드 없이 실행하고 결과(Outcome)만 돌려준다.
    /// null = 진행 전 중단(미연결·이동 실패 등, 이미 안내함). 취소는 Cancelled 상태로 반환.</summary>
    private async Task<Automation.BatchUploadRunner.SubjectOutcome?> RunSubjectCoreAsync(
        SubjectSheet sheet, bool dryRun, NeisAutoFill.Core.ClassRef? subjectModeClass)
    {
        // 시작 확인창 없음 — 단건 입력은 저장을 안 하고(나이스 [저장]은 사용자가 직접),
        // 문제가 있으면 매칭창이, 끝나면 결과 대시보드가 뜨므로 사전 확인은 군더더기다.

        // 사전 점검+이동 게이트 (R9) — 앱이 못 푸는 상황만 안내하고, 화면이 다르면 스스로 이동.
        var navProg = new Progress<Automation.Abstractions.ProgressInfo>(p => Log(p.Message));
        if (await _session.EnsureReadyAsync(Automation.Abstractions.NeisTarget.Evaluation, navProg) is { } blocked)
        {
            ShowError(blocked);
            return null;
        }

        // 전담: 그 반·과목으로 나이스를 맞추고 [조회] — 배치와 같은 준비 헬퍼 (R10)
        // (콤보 찾기·선택 같은 중계는 화면에 안 보이게 — 진행은 아래 한 줄로만 알린다)
        if (subjectModeClass is { } cls)
        {
            Log($"{cls.Grade}학년 {cls.Class}반 {sheet.SubjectName} 화면을 준비하고 있어요…");
            if (await PrepareClassScreenAsync(cls, sheet.SubjectName, CancellationToken.None) is { } why)
            { ShowError(why); return null; }
        }

        _cts = new CancellationTokenSource();
        ProgressValue = 0;
        try
        {
            var report = await _engine.RunSubjectAsync(
                sheet, _scales.Active, dryRun, _progress, BuildResolveMatch(sheet), _cts.Token);
            var act = dryRun ? "확인" : "입력";
            // 요약은 학생(명) 단위 — 성적은 내부적으로 (학생×영역) 건으로 돌지만 사용자에겐 명수가 직관적.
            // 세부 목록(어느 영역인지)은 아래 줄들로 그대로 보여준다.
            int doneN = report.Done.Select(d => (d.No, d.Name)).Distinct().Count();
            int skipN = report.Skipped.Select(s => (s.No, s.Name)).Distinct().Count();
            int failN = report.Failed.Select(f => (f.No, f.Name)).Distinct().Count();
            var tail = (skipN + failN) > 0 ? $" (건너뜀 {skipN}명·안 됨 {failN}명)" : "";
            Log($"✔ {sheet.SubjectName} {act} 완료 — {doneN}명{tail}");
            foreach (var s in report.Skipped) Log($"  · {s.No}번 {s.Name} '{s.Area}' 건너뜀 ({s.Reason})");
            foreach (var f in report.Failed) Log($"  · {f.No}번 {f.Name} '{f.Area}' 안 됨 ({f.Reason})");
            if (report.Missing.Count > 0)
                Log($"  일부 학생을 화면에서 못 찾았어요 — 나이스에서 확인해 주세요");
            if (!dryRun)
                Log("아직 저장 전이에요. 나이스에서 값 확인 후 [저장]을 눌러 주세요.");

            // 결과를 Outcome 으로 반환 — 대시보드 표시는 래퍼(RunSubjectAsync)가 담당
            var label = subjectModeClass is { } lc ? $"{lc.Grade}-{lc.Class} {sheet.SubjectName}" : sheet.SubjectName;
            return Helpers.UploadOutcomes.Single(label, doneN, skipN, failN, report.Failed, "입력할 값 없음");
        }
        catch (OperationCanceledException)
        {
            Log("⛔ 사용자 중지");
            var label = subjectModeClass is { } lc ? $"{lc.Grade}-{lc.Class} {sheet.SubjectName}" : sheet.SubjectName;
            return Helpers.UploadOutcomes.Cancelled(label);
        }
        catch (Exception ex)
        {
            Log($"오류: {ex.Message}");
            ShowError($"입력 중 오류가 발생했습니다.\n\n{ex.Message}");   // 로그 + 팝업 둘 다
            return null;
        }
    }

    // 매칭 확인 창구 — 분석·단계별 창·배치 이름 매핑 캐시를 전담 (R8, Helpers/MatchSession)
    private Helpers.MatchSession? _matchSession;
    private Helpers.MatchSession MatchSession => _matchSession ??= new Helpers.MatchSession(Log, () => _generatorSettings.Options.FastInput);

    private Func<MatchContext, Task<MatchDecision?>> BuildResolveMatch(SubjectSheet sheet, bool batch = false) =>
        MatchSession.ForGrades(sheet, batch);

    // ── 전과목 자동 입력 (Phase 5.5, A안: 과목별 검증 통과 시 자동 저장) ──

    public ICommand RunAllSubjectsCommand { get; private set; } = null!;

    // 입력 버튼 라벨 — 담임은 과목 축, 전담은 반 축(탭=반)이라 표현이 다르다 (F9 M9)
    /// <summary>단건 입력 버튼: 담임 "▶ 이 과목 입력" / 전담 "▶ 이 반 입력"(지금 반+과목 하나).</summary>
    public string RunOneLabel => IsSubjectMode ? "▶ 이 반 입력" : "▶ 이 과목 입력";
    /// <summary>배치 입력: 담임 "🚀 전과목 입력" / 전담 "🚀 이 과목 전체반 입력"(이 과목이 있는 모든 반).</summary>
    public string RunAllLabel => IsSubjectMode ? "🚀 이 과목 전체반 입력" : "🚀 전과목 입력";

    private async Task RunAllSubjectsAsync()
    {
        if (_session.ConnectCheck() is { } notConnected) { ShowError(notConnected); return; }

        // 전담: 탭=반이므로 "이 과목을 여러 반에 순회 입력" (담임은 아래 기존 경로 = 한 반에 과목 순회)
        if (IsSubjectMode) { await RunAllClassesAsync(); return; }

        var allSheets = Subjects.Select(s => s.Snapshot()).ToList();
        if (!allSheets.Any(s => s.Students.Any(st => st.Grades.Count > 0)))
        { ShowError("입력할 성적이 없습니다. 성적표에 등급을 먼저 입력해 주세요."); return; }

        // 교과별 평가 화면으로 먼저 이동해야 화면 과목 목록을 읽어 매칭할 수 있다 (게이트가 이동까지, R9)
        var navProg = new Progress<Automation.Abstractions.ProgressInfo>(p => Log(p.Message));
        if (await _session.EnsureReadyAsync(Automation.Abstractions.NeisTarget.Evaluation, navProg) is { } blocked)
        { ShowError(blocked); return; }

        // 과목 체크리스트 — 입력할 과목을 고르고, 자동 저장 동의도 이 창에서
        var picks = allSheets.Select(s =>
        {
            int n = s.Students.Sum(st => st.Grades.Count(g => !string.IsNullOrWhiteSpace(g.Value)));
            return new SubjectPick(s.SubjectName, n, n > 0 ? $"등급 {n}건 입력 예정" : "입력할 등급 없음");
        }).ToList();

        var bySubject = allSheets.ToDictionary(s => s.SubjectName);
        await new Helpers.BatchUploadFlow
        {
            Title = "전과목 나이스 입력",
            Description = "나이스에 입력할 과목을 선택하세요. 교과별 평가 화면으로 자동 이동해 과목마다 조회·입력·저장합니다.",
            TargetNoun = "과목",
            Unit = "건",
            SummaryTitle = "전과목 자동 입력 결과",
            Engine = _engine,
            Log = Log,
            NewCts = () => (_cts = new CancellationTokenSource()).Token,
            MapScreenSubjects = true,   // 화면 과목 목록을 읽어 매핑 자동 제안 (이름이 다르면 사용자가 창에서 고른다)
            OnStart = () => MatchSession.Reset(),   // 이름 매핑 캐시 초기화 (첫 과목에서 받고 이후 재사용)
            Session = MatchSession,   // 선택 창에서 매핑한 과목은 입력 때 "그대로 진행?"을 다시 안 묻는다
            RunTarget = async subjectName =>
            {
                ProgressValue = 0;
                var sheet = bySubject[subjectName];
                var report = await _engine.RunSubjectAsync(
                    sheet, _scales.Active, dryRun: false, _progress, BuildResolveMatch(sheet, batch: true), _cts!.Token);
                return new Automation.BatchUploadRunner.SubjectResult(
                    report.Done.Count, report.Failed, report.Skipped.Count,
                    report.Skipped.Any(s => s.Reason == "사용자 취소"));
            },
        }.RunAsync(picks);
    }

    /// <summary>전담 배치: 지금 보는 과목을, 고른 여러 반에 순회 입력 (F9 M9).
    /// 각 반마다 이동→과목→조회→입력, 검증 통과 시 그 반 성적 파일에 저장하고 다음 반으로.</summary>
    private async Task RunAllClassesAsync()
    {
        if (_currentSubject is null || Subjects.Count == 0)
        { ShowError("먼저 과목을 골라 [이동]하세요."); return; }

        // 각 반 탭 = 입력 후보. 등급이 하나라도 있는 반만 기본 체크
        var tabsByClass = Subjects.Where(s => s.OwnerClass is not null)
            .ToDictionary(s => s.OwnerClass!.Value.Key, s => s);
        var picks = Subjects.Where(s => s.OwnerClass is not null).Select(s =>
        {
            var sheet = s.Snapshot();
            int n = sheet.Students.Sum(st => st.Grades.Count(g => !string.IsNullOrWhiteSpace(g.Value)));
            var pick = new SubjectPick(s.OwnerClass!.Value.Key, n, n > 0 ? $"등급 {n}건 입력 예정" : "입력할 등급 없음");
            pick.IsChecked = n > 0;
            return pick;
        }).ToList();

        if (!picks.Any(p => p.EligibleCount > 0))
        { ShowError("입력할 성적이 없습니다. 반 탭에서 등급을 먼저 입력해 주세요."); return; }

        await new Helpers.BatchUploadFlow
        {
            Title = $"'{_currentSubject}' 여러 반 입력",
            Description = $"'{_currentSubject}' 성적을 입력할 반을 선택하세요. 각 반으로 이동·조회한 뒤 자동 입력합니다.",
            TargetNoun = "반",
            Unit = "건",
            SummaryTitle = $"'{_currentSubject}' 여러 반 입력 결과",
            Engine = _engine,
            Log = Log,
            NewCts = () => (_cts = new CancellationTokenSource()).Token,
            SwitchSubjects = false,   // 대상이 반("3-1")이라 러너의 과목 전환을 끈다 (이동·전환은 아래에서)
            // 한 반 입력: 그 반으로 이동·조회 후 입력, 통과하면 그 반 파일에 저장
            RunTarget = async classKey =>
            {
                ProgressValue = 0;
                var vm = tabsByClass[classKey];
                var sheet = vm.Snapshot();

                if (await PrepareClassScreenAsync(vm.OwnerClass!.Value, _currentSubject!, _cts!.Token) is { } why)
                    return Fail(why);

                var report = await _engine.RunSubjectAsync(
                    sheet, _scales.Active, dryRun: false, _progress, BuildResolveMatch(sheet), _cts.Token);

                // 나이스 [저장]은 러너가 검증 통과 시 일관되게 누른다 (여기서 또 누르면 이중 저장 → 실패)
                if (report.Failed.Count == 0) SaveSubjectTabsIfDirty();   // 로컬 파일 저장만
                return new Automation.BatchUploadRunner.SubjectResult(
                    report.Done.Count, report.Failed, report.Skipped.Count,
                    report.Skipped.Any(s => s.Reason == "사용자 취소"));
            },
        }.RunAsync(picks);
    }

    /// <summary>전담 성적 화면 준비 — 교과별 평가로 이동(이미 있으면 생략) → 반 → 과목 → [조회].
    /// 이동 직후엔 명단이 없으니 반드시 조회한다. 실패하면 사유 문구, 성공이면 null (단건·배치 공용, R10).</summary>
    private async Task<string?> PrepareClassScreenAsync(
        NeisAutoFill.Core.ClassRef cls, string subject, CancellationToken ct)
    {
        if (!await _engine.NavigateToAsync(Automation.Abstractions.NeisTarget.Evaluation,
                new Progress<Automation.Abstractions.ProgressInfo>(p => Log(p.Message)), ct))
            return "교과별 평가 화면으로 이동하지 못했어요.";
        var (okClass, whyClass) = await _engine.SelectClassAsync(cls.Grade, cls.Class, null, ct);
        if (!okClass) return $"{cls.Grade}학년 {cls.Class}반으로 이동하지 못했어요 — {whyClass}";
        var (okSubj, whySubj) = await _engine.SelectSubjectAsync(subject, ct);
        if (!okSubj)
        {
            // 콤보에 프로그램 과목명이 없으면(이름만 다름) 중단하지 않는다 — 입력 직전
            // 매칭 확인 창이 "화면 과목 ≠ 대상 과목, 그대로 입력?"을 묻고 사용자가 결정 (팝업 1회)
            if (whySubj != Automation.Abstractions.INeisEngine.SubjectNotInList)
                return $"'{subject}' 과목으로 바꾸지 못했어요 — {whySubj}";
            Log($"⚠ 나이스 과목 목록에 '{subject}'이(가) 없어요 — 현재 화면 과목에 입력할지 확인 창에서 묻습니다.");
        }
        var (okQ, whyQ) = await _engine.QueryAsync(ct);   // 콤보가 그대로여도 명단이 뜨도록 조회
        if (!okQ) return $"명단을 불러오지 못했어요 — {whyQ}";
        return null;
    }

    private static Automation.BatchUploadRunner.SubjectResult Fail(string reason) =>
        new(0, new[] { new SkipItem("", "", "", reason) }, 0, false);

    // ── 화면 진단 (Phase 5.5 셀렉터 실측용 — docs/보관_진단_검증도구.md) ──

    public ICommand InspectCommand { get; private set; } = null!;

    private bool _showDiagButton;
    /// <summary>진단 버튼 표시 여부 — 기본 숨김. ❓를 Ctrl+Shift+클릭하면 토글 (개발·문의용).</summary>
    public bool ShowDiagButton { get => _showDiagButton; set => SetProperty(ref _showDiagButton, value); }
    public void ToggleDiagButton() => ShowDiagButton = !ShowDiagButton;

    /// <summary>연간 시간표 자동입력 (개발 중) — 문서 → 검토 → 매핑 → 계획까지.</summary>

    private async Task InspectAsync()
    {
        if (!_engine.Connected) { ShowError("나이스 연결 후 사용하세요. [🌐 NEIS 접속]으로 브라우저를 여세요."); return; }
        try
        {
            // 3초 여유 — 그동안 나이스 창을 원하는 화면 상태로 두면 그 상태가 진단된다
            Log("3초 뒤 화면을 살펴봐요 — 나이스를 원하는 화면으로 두세요.");
            await Task.Delay(3000);
            Log("화면을 살펴보는 중…");
            var report = await _engine.InspectDomAsync();
            AppPaths.EnsureRoot();
            var file = Path.Combine(AppPaths.Root, $"dom_inspect_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(file, report);
            Log(report);
            Log($"진단 리포트 저장: {file}");
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
            catch { /* 열기 실패는 무시 — 파일은 저장됨 */ }
        }
        catch (Exception ex) { Log($"진단 오류: {ex.Message}"); }
    }

    public void Cancel() => _cts?.Cancel();

    private void OnProgress(ProgressInfo p)
    {
        if (!string.IsNullOrEmpty(p.Message)) Log(p.Message);
        if (p.Current is int c && p.Total is int t)
        {
            ProgressMax = Math.Max(t, 1);
            ProgressValue = c;
        }
    }

    private static void ShowError(string msg) =>
        MessageBox.Show(msg, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
}
