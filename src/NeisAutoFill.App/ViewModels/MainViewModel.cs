using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
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
        ProfileStore profiles)
    {
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
        OpenGeneratorCommand = new RelayCommand(OpenGenerator);
        OpenScaleEditorCommand = new RelayCommand(OpenScaleEditor);
        OpenRecentCommand = new RelayCommand<string>(p => { if (p is not null) LoadExcel(p); });
        OpenPlanEditorCommand = new RelayCommand(() => OpenPlanEditor());
        RunAllSubjectsCommand = new AsyncRelayCommand(RunAllSubjectsAsync);
        InspectCommand = new AsyncRelayCommand(InspectAsync);
        ExportGradesCommand = new RelayCommand(ExportGrades);
        HelpCommand = new AsyncRelayCommand(OpenHelpAsync);

        _showCriteriaPanel = appState.State.ShowCriteriaPanel;
        _logExpanded = appState.State.LogExpanded;

        if (_profiles.IsSubjectMode)
            InitSubjectAxis();        // 전담: 등록된 반 목록·첫 조합 로드
        else
            RestoreLastFiles();       // 담임: 최근 사용 자료 자동 로드 (없으면 조용히 넘어감)
        // 앱 시작부터 자동 연결·재연결 (이미 열린 브라우저도 자동 포착). 종료 시 함께 정리
        Application.Current.Exit += (_, _) => _connectLoopCts.Cancel();
        _ = AutoConnectLoopAsync(_connectLoopCts.Token);
    }

    private readonly CancellationTokenSource _connectLoopCts = new();

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

    /// <summary>
    /// 백그라운드 자동 연결 루프. 안 붙어 있으면 조용히 attach 시도(이미 열린 attach 가능 브라우저 자동 포착),
    /// 붙어 있으면 생존 확인해 끊기면 재연결. 사용자가 [② 연결] 을 따로 누를 필요가 없다.
    /// </summary>
    private async Task AutoConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_engine.Connected)
                {
                    try
                    {
                        await _engine.AttachAsync(ct).ConfigureAwait(false);
                        Ui(() => { SetConnected(true); Log("브라우저 자동 연결됨."); });
                    }
                    catch (Exception ex)
                    {
                        Diag.Swallow(ex, "자동연결 attach");   // 조용히 재시도하되, 사용자에겐 다음 할 일을 안내
                        // 브라우저 자체가 없음 vs 열렸지만 나이스 탭 없음 을 구분해 안내 (우리가 던진 메시지 기준)
                        var hint = ex is InvalidOperationException && (ex.Message.Contains("neis") || ex.Message.Contains("탭"))
                            ? "나이스 전용 브라우저는 열렸어요. 나이스에 로그인하고 [교과별 평가](또는 [학기말 종합의견])를 조회하면 자동으로 연결됩니다."
                            : "아직 연결되지 않았어요. [🌐 NEIS 접속] 버튼으로 전용 브라우저를 여세요. (평소 쓰던 Edge 를 그냥 열면 연결되지 않습니다)";
                        Ui(() => ConnectionHint = hint);
                    }
                }
                else if (!await _engine.IsAliveAsync().ConfigureAwait(false))
                {
                    Ui(() => { SetConnected(false); Log("브라우저 연결이 끊어졌습니다. 재연결을 시도합니다."); });
                }
            }
            catch (Exception ex) { Diag.Swallow(ex, "자동연결 루프"); }   // 루프는 어떤 경우에도 죽지 않는다

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.Invoke(a);

    private void SetConnected(bool on)
    {
        ConnectionText = on ? "연결됨" : "미연결";
        ConnectionBrush = new SolidColorBrush(on ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xEF, 0x44, 0x44));
        IsConnected = on;
        if (on) ConnectionHint = "";   // 연결되면 안내 배너 숨김
    }

    private bool _isConnected;
    /// <summary>나이스 연결 여부 — 입력 버튼 활성/[NEIS 접속] 버튼 표시 제어 (U3·U5).</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(ShowConnectionHint));
                RefreshNextStep();
            }
        }
    }

    private string _connectionHint = "";
    /// <summary>미연결 시 다음에 뭘 해야 하는지 안내 (실패 원인별). 연결되면 빈 문자열.</summary>
    public string ConnectionHint
    {
        get => _connectionHint;
        private set
        {
            if (SetProperty(ref _connectionHint, value))
            {
                OnPropertyChanged(nameof(ShowConnectionHint));
                OnPropertyChanged(nameof(ShowNextStep));   // 연결 배너와 상호배타
            }
        }
    }

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
                return "다음: 나이스에 입력하려면 [🌐 NEIS 접속]으로 로그인·조회해 연결하세요. 서술문은 [✨ 교과학습] 창에서 생성합니다.";
            return "준비 완료! 과목 탭에서 [▶ 이 과목 입력] 또는 [🚀 전과목 입력]으로 나이스에 넣으세요. 서술문은 [✨ 교과학습] 창에서.";
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


    /// <summary>드래그앤드롭된 평가계획 문서(pdf/hwp/hwpx) → 편집 창 열고 AI 가져오기 시작.</summary>
    public void ImportPlanDocument(string path) => OpenPlanEditor(path);

    private void OpenPlanEditor(string? importPath = null)
    {
        // 담임 import: 과목 목록 인식 → 선택 콜백 → 고른 과목만 (F9 M4b)
        var importer = (Func<string, IProgress<string>,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>?,
            Task<IReadOnlyList<SubjectPlan>>>)
            ((path, progress, select) => new Generator.GasPlanImporter(AppHttp.Long, _generatorSettings.Options)
                .ImportAsync(path, _scales.Active, progress, select));

        // 전담 import: (학년·과목) 단위 인식 → 선택 콜백 → 학년별 세트 (F9 M4b)
        var unitImporter = (Func<string, IProgress<string>,
            Func<IReadOnlyList<NeisAutoFill.Core.PlanUnit>, Task<IReadOnlyList<NeisAutoFill.Core.PlanUnit>?>>?,
            Task<IReadOnlyList<Generator.GasPlanImporter.GradePlanSet>>>)
            ((path, progress, select) => new Generator.GasPlanImporter(AppHttp.Long, _generatorSettings.Options)
                .ImportUnitsAsync(path, _scales.Active, progress, select));

        // 전담: 자료 준비가 반별 명단·학년별 계획을 직접 저장(SubjectModeStore) — 담임 워크스페이스 저장 안 탐
        if (_profiles.IsSubjectMode)
        {
            var store = new SubjectModeStore(_scales.Active);
            // 메인에서 보던 반(예: 5-1)을 자료준비 기본값으로 — 안에서 학년·반 전환 가능
            var svm = new PlanEditorViewModel(Array.Empty<SubjectPlan>(), Array.Empty<(string, string)>(),
                _scales.Active, importer, store, _currentClass, unitImporter);
            var swin = new PlanEditorWindow(svm) { Owner = Application.Current.MainWindow };
            if (importPath is not null) swin.Loaded += async (_, _) => await svm.ImportPlanFileAsync(importPath);
            swin.ShowDialog();
            svm.SaveSubjectMode();   // 현재 반·학년 저장 (전환 시마다 이미 저장되지만 마지막 것도)
            Log("전담 명단·평가계획 저장 완료");
            return;
        }

        // 담임(기존): 명단 없으면 열린 성적파일 명단 재사용
        var roster = _workspace.Roster;
        if (roster.Count == 0 && Subjects.Count > 0)
            roster = Subjects[0].Snapshot().Students.Select(s => (s.No, s.Name)).ToList();

        var vm = new PlanEditorViewModel(_workspace.Plans, roster, _scales.Active, importer);
        var win = new PlanEditorWindow(vm) { Owner = Application.Current.MainWindow };
        if (importPath is not null)
            win.Loaded += async (_, _) => await vm.ImportPlanFileAsync(importPath);
        if (win.ShowDialog() != true) return;

        var built = vm.Build(out var error);
        if (built is null) { ShowError(error ?? "편집 내용을 읽지 못했습니다."); return; }

        var (savedPath, saveError) = _workspace.SavePlan(built.Value.Plans, built.Value.Roster);
        if (saveError is not null)
        {
            ShowError($"평가계획 저장 실패: {saveError}\n(파일이 엑셀에서 열려 있으면 닫고 다시 시도하세요)");
            return;
        }
        Log($"명단·평가계획 저장: {Path.GetFileName(savedPath!)}");
        LoadPlan(savedPath!);   // 저장본을 다시 읽어 반영 (엑셀 직접 수정과 같은 경로)
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

    // ── 전담: 메인 학년·반 축 (F9 M5) — 학년·반 분리, [이동]으로 명시 적용 ──
    /// <summary>전담 모드인가 — 상단 학년·반 콤보 표시.</summary>
    public bool IsSubjectMode => _profiles.IsSubjectMode;

    private IReadOnlyList<NeisAutoFill.Core.ClassRef> _allClasses = Array.Empty<NeisAutoFill.Core.ClassRef>();

    /// <summary>등록된 학년 목록.</summary>
    public ObservableCollection<int> GradeChoices { get; } = new();
    /// <summary>선택된 학년의 반 목록 (반 콤보). 학년 콤보를 바꾸면 갱신되지만 자료는 안 바뀐다.</summary>
    public ObservableCollection<string> ClassChoices { get; } = new();

    private int _pickGrade;
    /// <summary>학년 콤보 선택값 — 바꾸면 반 목록만 갱신(자료 로드 안 함).</summary>
    public int PickGrade
    {
        get => _pickGrade;
        set { if (SetProperty(ref _pickGrade, value)) RefreshClassChoices(); }
    }

    private string? _pickClass;
    /// <summary>반 콤보 선택값 — 선택만. [이동]을 눌러야 전환된다.</summary>
    public string? PickClass { get => _pickClass; set => SetProperty(ref _pickClass, value); }

    /// <summary>[이동] — 선택한 학년·반으로 성적표 전환.</summary>
    public ICommand GoUnitCommand => _goUnit ??= new RelayCommand(GoUnit,
        () => PickGrade > 0 && !string.IsNullOrEmpty(PickClass));
    private ICommand? _goUnit;

    private NeisAutoFill.Core.ClassRef? _currentClass;   // 실제로 로드된 반

    private void RefreshClassChoices()
    {
        ClassChoices.Clear();
        foreach (var c in _allClasses.Where(c => c.Grade == PickGrade)) ClassChoices.Add(c.Class);
        PickClass = ClassChoices.FirstOrDefault();
        (_goUnit as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void GoUnit()
    {
        if (PickGrade <= 0 || string.IsNullOrEmpty(PickClass)) return;
        var c = new NeisAutoFill.Core.ClassRef(PickGrade, PickClass!);
        _currentClass = c;
        LoadUnitForCurrentAxis();

        // 나이스에 연결돼 있으면 그 학년·반으로 화면도 이동 (F9 M6). 연결 전이면 로컬 전환만.
        if (IsConnected)
            _ = NavigateNeisToClassAsync(c);
    }

    /// <summary>나이스 조회조건을 현재 반의 학년·반으로 맞춘다 (전담 F9 M6).
    /// 실패해도 로컬 전환은 유지 — 원인을 로그로만 남긴다(중단형 개입 없음).</summary>
    private async Task NavigateNeisToClassAsync(NeisAutoFill.Core.ClassRef c)
    {
        var progress = new Progress<Automation.Abstractions.ProgressInfo>(p => Ui(() => Log(p.Message)));
        try
        {
            var (ok, why) = await _engine.SelectClassAsync(c.Grade, c.Class, progress).ConfigureAwait(false);
            Ui(() => Log(ok
                ? $"나이스 이동 완료: {c.Grade}-{c.Class}"
                : $"⚠ 나이스 {c.Grade}-{c.Class} 이동 실패 — {why}"));
        }
        catch (Exception ex)
        {
            Ui(() => Log($"⚠ 나이스 이동 중 오류: {ex.Message}"));
        }
    }

    /// <summary>전담 모드 초기화 — 등록된 학년·반 목록을 채우고 첫 조합을 로드 (앱 시작 시).</summary>
    private void InitSubjectAxis()
    {
        if (!IsSubjectMode) return;
        var store = new SubjectModeStore(_scales.Active);
        _allClasses = store.ListClasses();
        foreach (var g in _allClasses.Select(c => c.Grade).Distinct().OrderBy(g => g)) GradeChoices.Add(g);
        _pickGrade = GradeChoices.FirstOrDefault();
        OnPropertyChanged(nameof(PickGrade));
        RefreshClassChoices();
        if (_pickGrade > 0 && PickClass is not null) GoUnit();   // 첫 조합 자동 로드 (시작 시엔 편함)
    }

    /// <summary>현재 (반) 축으로 성적표를 구성한다. 그 반 명단 + 해당 학년 계획의 모든 과목을 합쳐,
    /// 과목마다 SubjectSheet 를 만들어 탭으로 표시. 성적·서술문은 조합별 경로에 저장.</summary>
    private void LoadUnitForCurrentAxis()
    {
        if (!IsSubjectMode || _currentClass is not { } c) return;

        // 편집 중이던 조합의 성적 저장 (있으면)
        if (_currentUnitGradePath is not null && Subjects.Any(s => s.IsDirty))
            _workspace.SaveGrades(Subjects.Select(s => s.Snapshot()).ToList(), _currentUnitGradePath);

        var store = new SubjectModeStore(_scales.Active);
        var roster = store.LoadRoster(c);
        var plans = store.LoadPlan(c.Grade);   // 그 학년의 과목별 계획

        // 조합별 저장 경로 지정 (성적·서술문). 대표 과목 조합으로 폴더 결정 — 반 단위 폴더 사용.
        var wsRoot = AppPaths.EnsureWorkspaceRoot();
        var unit0 = new NeisAutoFill.Core.TeachingUnit(c.Grade, c.Class, plans.FirstOrDefault()?.SubjectName ?? "과목");
        _currentUnitGradePath = NeisAutoFill.Core.SubjectModePaths.UnitGradeFile(wsRoot, unit0);
        var narrPath = NeisAutoFill.Core.ProfilePaths.DataFile(AppPaths.Root, $"{c.Grade}-{c.Class}", "narratives.json");
        _narratives.SwitchTo(narrPath);

        // 과목마다 성적표 구성 (기존 성적 있으면 이월)
        IReadOnlyList<SubjectSheet>? existing = null;
        if (File.Exists(_currentUnitGradePath))
            try { existing = NeisAutoFill.Excel.WorkbookLoader.Load(_currentUnitGradePath); }
            catch { /* 손상 시 새로 */ }
        var sheets = plans.Select(p =>
        {
            var old = existing?.FirstOrDefault(s => s.SubjectName == p.SubjectName);
            return NeisAutoFill.Core.SheetSynchronizer.BuildUnitSheet(p, roster, old);
        }).ToList();

        ReplaceSubjects(sheets, keepSelected: false);
        Log($"전담 {c.Grade}-{c.Class} — {plans.Count}과목, 명단 {roster.Count}명");
    }

    private string? _currentUnitGradePath;

    private SubjectViewModel? _selectedSubject;
    public SubjectViewModel? SelectedSubject
    {
        get => _selectedSubject;
        set { if (SetProperty(ref _selectedSubject, value)) RefreshCriteriaPanel(); }
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
        }
    }

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
        var plan = subjectName is null ? null : _workspace.Plans.FirstOrDefault(p => p.SubjectName == subjectName);
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

    private string _connectionText = "미연결";
    public string ConnectionText { get => _connectionText; set => SetProperty(ref _connectionText, value); }

    private Brush _connectionBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    public Brush ConnectionBrush { get => _connectionBrush; set => SetProperty(ref _connectionBrush, value); }

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
    public ICommand OpenGeneratorCommand { get; }

    private void OpenGenerator()
    {
        try
        {
            _generatorVm ??= new GeneratorViewModel(
                () => Subjects.Select(s => s.Snapshot()).ToList(),
                () => _workspace.Plans,
                _scales, _generatorSettings, _narratives, _generationQueue, _narrativeMirror, _engine, Log);
            _generatorVm.RefreshSubjects();   // 메인에서 로드된 성적·평가계획을 자동 반영
            new GeneratorWindow(_generatorVm) { Owner = Application.Current.MainWindow }.Show();
        }
        catch (Exception ex)
        {
            Log($"생성기 열기 오류: {ex.Message}");
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
    public void NotifyGradesEdited()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
        RefreshNextStep();   // 등급 입력이 시작되면 다음 안내가 바뀐다 (첫 등급 → 연결 유도)
    }

    /// <summary>편집이 잦아들면 저장 대상 파일에 조용히 저장. 실패(파일 잠금 등)는 로그만 남기고 dirty 유지.</summary>
    private void AutoSaveGrades()
    {
        // 전담: 현재 조합의 성적 파일에 저장
        var savePath = IsSubjectMode ? _currentUnitGradePath : _workspace.GradeFilePath;
        if (savePath is null || !Subjects.Any(s => s.IsDirty)) return;
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

        // 저장 대상 파일이 있으면 조용히 자동 저장 (평소 자동 저장과 같은 동작)
        if (_workspace.GradeFilePath is not null)
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

    public async Task RunSubjectAsync(SubjectSheet sheet, bool dryRun)
    {
        if (!_engine.Connected)
        {
            ShowError("나이스에 아직 연결되지 않았습니다. [① NEIS 접속]으로 브라우저를 열고 로그인·조회하면 자동으로 연결됩니다.");
            return;
        }
        if (!dryRun)
        {
            var ok = MessageBox.Show(
                $"'{sheet.SubjectName}' {sheet.Students.Count}명의 평가를 나이스 화면에 입력합니다.\n" +
                "(저장은 하지 않으며, 확인 후 나이스에서 직접 [저장]을 누르세요)\n\n" +
                $"나이스 화면이 '{sheet.SubjectName}' 조회 상태인가요?",
                "확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
        }

        _cts = new CancellationTokenSource();
        ProgressValue = 0;
        try
        {
            var report = await _engine.RunSubjectAsync(
                sheet, _scales.Active, dryRun, _progress, BuildResolveMatch(sheet), _cts.Token);
            Log(new string('=', 50));
            Log($"[{sheet.SubjectName}] {(dryRun ? "검증" : "입력")} 완료: " +
                $"성공 {report.Done.Count} / 건너뜀 {report.Skipped.Count} / 실패 {report.Failed.Count}");
            foreach (var s in report.Skipped) Log($"  건너뜀: {s.No}번 {s.Name} '{s.Area}' ({s.Reason})");
            foreach (var f in report.Failed) Log($"  실패: {f.No}번 {f.Name} '{f.Area}' ({f.Reason})");
            if (report.Missing.Count > 0)
                Log($"  ⚠ 화면에서 파악 못한 행 {string.Join(",", report.Missing)} — 나이스에서 직접 확인 필요");
            if (!dryRun)
                Log("※ 저장하지 않았습니다. 나이스에서 값 확인 후 [저장]을 눌러주세요.");

            // 건너뜀·실패·누락이 있으면 팝업으로도 알림 (로그를 못 볼 수 있으므로)
            int problems = report.Skipped.Count + report.Failed.Count + report.Missing.Count;
            if (problems > 0)
            {
                var lines = new System.Text.StringBuilder();
                lines.AppendLine($"[{sheet.SubjectName}] {(dryRun ? "검증" : "입력")} 결과");
                lines.AppendLine($"성공 {report.Done.Count} / 건너뜀 {report.Skipped.Count} / 실패 {report.Failed.Count}");
                lines.AppendLine();
                foreach (var f in report.Failed) lines.AppendLine($"✗ 실패 {f.No}번 {f.Name} '{f.Area}' — {f.Reason}");
                foreach (var s in report.Skipped) lines.AppendLine($"· 건너뜀 {s.No}번 {s.Name} '{s.Area}' — {s.Reason}");
                if (report.Missing.Count > 0) lines.AppendLine($"⚠ 화면에서 못 읽은 행: {string.Join(",", report.Missing)}");
                lines.AppendLine();
                lines.Append("자세한 내용은 아래 로그를 확인하세요.");
                MessageBox.Show(lines.ToString(),
                    report.Failed.Count > 0 ? "일부 실패" : "일부 건너뜀",
                    MessageBoxButton.OK,
                    report.Failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) { Log("⛔ 사용자 중지"); }
        catch (Exception ex)
        {
            Log($"오류: {ex.Message}");
            ShowError($"입력 중 오류가 발생했습니다.\n\n{ex.Message}");   // 로그 + 팝업 둘 다
        }
    }

    /// <summary>화면 파악 후 매칭 검토 콜백 — 문제 없으면 창 없이 진행, 있으면 미리보기 창에서 사용자 결정.</summary>
    private Func<MatchContext, Task<MatchDecision?>> BuildResolveMatch(SubjectSheet sheet) => ctx =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var issues = Core.Matching.MatchAnalyzer.Analyze(
                ctx.ScreenSubject, ctx.TargetSubject, ctx.RowMap, sheet.Students, sheet.Areas);
            if (issues.Clean)
                return new MatchDecision(Core.Matching.StudentMatcher.MatchMode.ByName);

            // 과목명만 다르고 학생·영역은 정상 → 복잡한 매핑 창 대신 "그래도 진행?" 만 묻는다
            if (issues.SubjectOnlyMismatch)
            {
                var ok = MessageBox.Show(
                    $"화면 과목은 '{issues.ScreenSubject}'인데 입력 대상은 '{sheet.SubjectName}'입니다.\n" +
                    "학생·영역은 화면과 일치합니다. 이 화면에 그대로 입력할까요?",
                    "과목 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                return ok ? new MatchDecision(Core.Matching.StudentMatcher.MatchMode.ByName) : (MatchDecision?)null;
            }

            var vm = new MatchPreviewViewModel(issues, sheet);
            var win = new MatchPreviewWindow(vm) { Owner = Application.Current.MainWindow };
            return win.ShowDialog() == true ? vm.BuildDecision() : null;
        }).Task;

    // ── 전과목 자동 입력 (Phase 5.5, A안: 과목별 검증 통과 시 자동 저장) ──

    public ICommand RunAllSubjectsCommand { get; private set; } = null!;

    private async Task RunAllSubjectsAsync()
    {
        if (!_engine.Connected)
        {
            ShowError("나이스에 아직 연결되지 않았습니다. [① NEIS 접속] 후 로그인·조회하면 자동으로 연결됩니다.");
            return;
        }
        var allSheets = Subjects.Select(s => s.Snapshot()).ToList();
        if (!allSheets.Any(s => s.Students.Any(st => st.Grades.Count > 0)))
        { ShowError("입력할 성적이 없습니다. 성적표에 등급을 먼저 입력해 주세요."); return; }

        // 과목 체크리스트 — 입력할 과목을 고르고, 자동 저장 동의도 이 창에서
        var picks = allSheets.Select(s =>
        {
            int n = s.Students.Sum(st => st.Grades.Count(g => !string.IsNullOrWhiteSpace(g.Value)));
            return new SubjectPick(s.SubjectName, n, n > 0 ? $"등급 {n}건 입력 예정" : "입력할 등급 없음");
        }).ToList();
        // 화면 과목 목록을 읽어 매핑 자동 제안 (이름이 다르면 사용자가 창에서 고른다)
        await PopulateScreenMappingAsync(picks);

        var win = new BatchGenerateWindow(picks,
            title: "전과목 나이스 입력",
            description: "나이스에 입력할 과목을 선택하세요. 나이스 화면이 [교과별 평가] 조회 화면인지 확인한 뒤 시작하세요.",
            startLabel: "🚀 입력 시작",
            warning: "각 과목 입력 후 값 검증을 통과하면 나이스 [저장]을 자동으로 누르고 다음 과목으로 넘어갑니다. " +
                     "검증에 실패한 과목은 저장하지 않고 그 자리에서 중단합니다.")
        { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        var chosen = picks.Where(p => p.IsChecked).ToList();
        var chosenNames = chosen.Select(p => p.Name).ToHashSet();
        var sheets = allSheets.Where(s => chosenNames.Contains(s.SubjectName)).ToList();
        if (sheets.Count == 0) return;

        _cts = new CancellationTokenSource();
        var bySubject = sheets.ToDictionary(s => s.SubjectName);
        var targets = BuildTargets(chosen);   // 내 과목 → 화면 과목 매핑 반영

        // runSubject 를 델리게이트로 빼 재시도 때 그대로 재사용한다 (엔진 경로 동일)
        Func<string, Task<Automation.BatchUploadRunner.SubjectResult>> runSubject = async subjectName =>
        {
            ProgressValue = 0;
            var sheet = bySubject[subjectName];
            var report = await _engine.RunSubjectAsync(
                sheet, _scales.Active, dryRun: false, _progress, BuildResolveMatch(sheet), _cts.Token);
            return new Automation.BatchUploadRunner.SubjectResult(
                report.Done.Count, report.Failed, report.Skipped.Count,
                report.Skipped.Any(s => s.Reason == "사용자 취소"));
        };

        var outcomes = await Automation.BatchUploadRunner.RunAsync(
            targets, _engine, runSubject, Log, unit: "건", _cts.Token);

        Log(new string('=', 50));
        Log("전과목 자동 입력 결과:");
        foreach (var s in Automation.BatchUploadRunner.Summarize(outcomes)) Log("  " + s);

        // 재시도: 실패·미도달 과목만 같은 경로로 (표시명 → 매핑된 화면명 복원)
        var screenByDisplay = targets.ToDictionary(t => t.Display, t => t.Screen);
        BatchResultWindow.ShowResult(outcomes, "건",
            retry: async subs =>
            {
                _cts = new CancellationTokenSource();
                var rt = subs.Select(d => new Automation.BatchUploadRunner.SubjectTarget(
                    d, screenByDisplay.TryGetValue(d, out var sc) ? sc : d)).ToList();
                return await Automation.BatchUploadRunner.RunAsync(rt, _engine, runSubject, Log, "건", _cts.Token);
            },
            owner: Application.Current.MainWindow);
    }

    /// <summary>선택된 과목들로 (내 과목 → 화면 과목) 대상 목록을 만든다. 매핑이 없거나 '미선택'이면 같은 이름으로 폴백.</summary>
    private static List<Automation.BatchUploadRunner.SubjectTarget> BuildTargets(IEnumerable<SubjectPick> chosen) =>
        chosen.Select(p =>
        {
            var screen = p.HasScreenOptions && p.ScreenSubject != SubjectPick.NoScreenMatch
                ? p.ScreenSubject : p.Name;
            return new Automation.BatchUploadRunner.SubjectTarget(p.Name, screen);
        }).ToList();

    /// <summary>화면 과목 콤보 목록을 읽어 각 pick 에 자동 매핑을 채운다. 못 읽으면(오프라인 등) 매핑 UI 없이 같은 이름 사용.</summary>
    private async Task PopulateScreenMappingAsync(IReadOnlyList<SubjectPick> picks)
    {
        try
        {
            // ★ 이전 작업에서 취소된 _cts.Token 을 쓰면 콤보 읽기가 즉시 취소돼 매핑이 안 뜬다 → None 으로 읽는다(서술문과 동일)
            var screen = await _engine.ReadSubjectOptionsAsync();
            if (screen.Count == 0) return;   // 콤보를 못 읽음 → 기존처럼 같은 이름으로 진행
            var suggestions = Core.SubjectMapper.Suggest(picks.Select(p => p.Name).ToList(), screen);
            for (int i = 0; i < picks.Count; i++)
                picks[i].SetScreenMapping(screen, suggestions[i].Screen, suggestions[i].Auto);
        }
        catch (Exception ex) { Diag.Swallow(ex, "전과목(등급) 화면 과목 매핑"); }   // 같은 이름으로 진행
    }

    // ── 화면 진단 (Phase 5.5 셀렉터 실측용 — docs/보관_진단_검증도구.md) ──

    public ICommand InspectCommand { get; private set; } = null!;

    private async Task InspectAsync()
    {
        if (!_engine.Connected) { ShowError("나이스 연결 후 사용하세요."); return; }
        try
        {
            var report = await _engine.InspectDomAsync();
            Log(report);
            AppPaths.EnsureRoot();
            var file = Path.Combine(AppPaths.Root, $"dom_inspect_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(file, report);
            Log($"진단 리포트 저장: {file}");
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
