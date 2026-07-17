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

    public MainViewModel(INeisEngine engine, IScaleStore scales,
        GeneratorSettingsStore generatorSettings, NarrativeStore narratives,
        AppStateStore appState, GenerationQueue generationQueue, NarrativeMirror narrativeMirror,
        Automation.EngineOptions engineOptions)
    {
        _engine = engine;
        _scales = scales;
        _generatorSettings = generatorSettings;
        _narratives = narratives;
        _appState = appState;
        _generationQueue = generationQueue;
        _narrativeMirror = narrativeMirror;
        _engineOptions = engineOptions;

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
        LoadPlanCommand = new RelayCommand(OpenPlan);
        SaveStep1TemplateCommand = new RelayCommand(SaveStep1Template);
        SaveStep2TemplateCommand = new RelayCommand(SaveStep2Template);
        OpenDataPrepCommand = new RelayCommand(() =>
            new DataPrepWindow(this) { Owner = Application.Current.MainWindow }.ShowDialog());
        OpenRecentCommand = new RelayCommand<string>(p => { if (p is not null) LoadExcel(p); });
        OpenPlanEditorCommand = new RelayCommand(() => OpenPlanEditor());
        RunAllSubjectsCommand = new AsyncRelayCommand(RunAllSubjectsAsync);
        InspectCommand = new AsyncRelayCommand(InspectAsync);

        _showCriteriaPanel = appState.State.ShowCriteriaPanel;

        RestoreLastFiles();           // 최근 사용 자료 자동 로드 (없으면 조용히 넘어감)
        _ = AutoConnectLoopAsync();   // 앱 시작부터 자동 연결·재연결 (이미 열린 브라우저도 자동 포착)
    }

    // ── 최근 파일 · 자동 로드 ──────────────────

    public ICommand OpenRecentCommand { get; }

    /// <summary>최근 파일 메뉴 항목 (실존 파일만, 평가계획서·성적파일 구분).</summary>
    public IReadOnlyList<(string Path, string Display, bool IsPlan)> RecentEntries =>
        _appState.ExistingRecentPlans().Select(p => (p, Path.GetFileName(p), true))
        .Concat(_appState.ExistingRecentGrades().Select(p => (p, Path.GetFileName(p), false)))
        .ToList();

    /// <summary>시작 시 마지막으로 쓰던 성적파일·평가계획서를 복원. 실패는 로그만 (팝업 없음).
    /// 성적을 먼저 열어야 평가계획 로드가 성적표를 새로 만들지 않는다.</summary>
    private void RestoreLastFiles()
    {
        var grades = _appState.State.LastGradePath;
        if (grades is not null && File.Exists(grades)) LoadGrades(grades, silent: true);

        var plan = _appState.State.LastPlanPath;
        if (plan is not null && File.Exists(plan)) LoadPlan(plan, silent: true);
    }

    /// <summary>
    /// 백그라운드 자동 연결 루프. 안 붙어 있으면 조용히 attach 시도(이미 열린 attach 가능 브라우저 자동 포착),
    /// 붙어 있으면 생존 확인해 끊기면 재연결. 사용자가 [② 연결] 을 따로 누를 필요가 없다.
    /// </summary>
    private async Task AutoConnectLoopAsync()
    {
        while (true)
        {
            try
            {
                if (!_engine.Connected)
                {
                    try
                    {
                        await _engine.AttachAsync().ConfigureAwait(false);
                        Ui(() => { SetConnected(true); Log("브라우저 자동 연결됨."); });
                    }
                    catch { /* 아직 attach 가능한 브라우저 없음 — 조용히 재시도 */ }
                }
                else if (!await _engine.IsAliveAsync().ConfigureAwait(false))
                {
                    Ui(() => { SetConnected(false); Log("브라우저 연결이 끊어졌습니다. 재연결을 시도합니다."); });
                }
            }
            catch { /* 루프는 어떤 경우에도 죽지 않는다 */ }

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.Invoke(a);

    private void SetConnected(bool on)
    {
        ConnectionText = on ? "연결됨" : "미연결";
        ConnectionBrush = new SolidColorBrush(on ? Color.FromRgb(0x22, 0xC5, 0x5E) : Color.FromRgb(0xEF, 0x44, 0x44));
    }

    public ICommand OpenDataPrepCommand { get; }

    // ── 평가계획서 (과목·영역·기준 + 학생명단) ──
    private IReadOnlyList<SubjectPlan> _plans = Array.Empty<SubjectPlan>();
    private IReadOnlyList<(string No, string Name)> _roster = Array.Empty<(string, string)>();
    private string? _planFilePath;   // 인앱 편집 저장 대상

    public IReadOnlyList<SubjectPlan> Plans => _plans;

    // ── 명단·평가계획 인앱 편집 ──────────────
    public ICommand OpenPlanEditorCommand { get; }

    private static readonly System.Net.Http.HttpClient ImportHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>드래그앤드롭된 평가계획 문서(pdf/hwp/hwpx) → 편집 창 열고 AI 가져오기 시작.</summary>
    public void ImportPlanDocument(string path) => OpenPlanEditor(path);

    private void OpenPlanEditor(string? importPath = null)
    {
        // 평가계획서에 명단이 없으면 열려 있는 성적파일의 학생 명단을 재사용
        var roster = _roster;
        if (roster.Count == 0 && Subjects.Count > 0)
            roster = Subjects[0].Sheet.Students.Select(s => (s.No, s.Name)).ToList();

        var vm = new PlanEditorViewModel(_plans, roster, _scales.Active,
            importer: async (path, progress) => await new Generator.GasPlanImporter(ImportHttp, _generatorSettings.Options)
                .ImportAsync(path, _scales.Active, progress));
        var win = new PlanEditorWindow(vm) { Owner = Application.Current.MainWindow };
        if (importPath is not null)
            win.Loaded += async (_, _) => await vm.ImportPlanFileAsync(importPath);
        if (win.ShowDialog() != true) return;

        var built = vm.Build(out var error);
        if (built is null) { ShowError(error ?? "편집 내용을 읽지 못했습니다."); return; }

        var path = _planFilePath ?? Path.Combine(AppPaths.EnsureWorkspace(), "평가계획서.xlsx");
        try
        {
            PlanWorkbookWriter.Write(path, built.Value.Plans, built.Value.Roster, _scales.Active);
            Log($"명단·평가계획 저장: {Path.GetFileName(path)}");
            LoadPlan(path);   // 저장본을 다시 읽어 반영 (엑셀 직접 수정과 같은 경로)
        }
        catch (Exception ex)
        {
            ShowError($"평가계획 저장 실패: {ex.Message}\n(파일이 엑셀에서 열려 있으면 닫고 다시 시도하세요)");
        }
    }

    private string _planName = "평가계획서 없음";
    public string PlanName { get => _planName; set => SetProperty(ref _planName, value); }

    public ICommand LoadPlanCommand { get; }
    public ICommand SaveStep1TemplateCommand { get; }
    public ICommand SaveStep2TemplateCommand { get; }

    private void OpenPlan()
    {
        var dlg = new OpenFileDialog { Filter = "Excel|*.xlsx;*.xlsm", Title = "평가계획서 선택" };
        if (dlg.ShowDialog() == true) LoadPlan(dlg.FileName);
    }

    private void LoadPlan(string path, bool silent = false)
    {
        try
        {
            _plans = PlanWorkbookLoader.Load(path, _scales.Active);
            _roster = PlanWorkbookLoader.LoadRoster(path);
            PlanName = Path.GetFileName(path);
            _planFilePath = path;
            _appState.TouchPlan(path);
            OnPropertyChanged(nameof(RecentEntries));
            RefreshCriteriaPanel();
            Log($"평가계획서 로드: {PlanName} " +
                $"({string.Join(", ", _plans.Select(p => $"{p.SubjectName} {p.Domains.Count}영역"))}" +
                (_roster.Count > 0 ? $" / 명단 {_roster.Count}명)" : ")"));
            SyncGradeTableWithPlan();   // 성적표 없으면 생성, 있으면 명단·영역 변경 동기화 (성적 보존)
        }
        catch (Exception ex)
        {
            if (silent) Log($"⚠ 최근 평가계획서를 다시 열지 못했습니다: {ex.Message}");
            else ShowError($"평가계획서 오류: {ex.Message}");
        }
    }

    private void SaveStep1Template()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel|*.xlsx",
            FileName = "평가계획서_양식.xlsx",
            Title = "평가계획서 양식 저장",
            InitialDirectory = AppPaths.EnsureWorkspace(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TemplateWriter.WriteStep1Template(dlg.FileName, _scales.Active);
            Log($"평가계획서 양식 저장: {Path.GetFileName(dlg.FileName)} — " +
                "[학생명단]에 명단을 넣고 과목 시트에 평가기준을 채운 뒤 [평가계획서 열기]로 불러오세요.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void SaveStep2Template()
    {
        if (_plans.Count == 0)
        {
            ShowError("먼저 [평가계획서 열기]로 작성된 평가계획서를 불러오세요.");
            return;
        }
        var dlg = new SaveFileDialog
        {
            Filter = "Excel|*.xlsx",
            FileName = $"성적입력양식_{_plans.Count}개과목.xlsx",
            Title = "성적입력 양식 저장",
            InitialDirectory = AppPaths.EnsureWorkspace(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            TemplateWriter.WriteStep2Template(dlg.FileName, _plans, _roster);
            Log($"성적입력 양식 저장: {Path.GetFileName(dlg.FileName)} — " +
                "성적을 채운 뒤 [성적파일 열기]로 불러오세요.");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    public ObservableCollection<SubjectViewModel> Subjects { get; } = new();

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

    public sealed record CriteriaLevelView(string Grade, string Text);
    public sealed record CriteriaDomainView(string Domain, string? Achievement, IReadOnlyList<CriteriaLevelView> Levels);

    private IReadOnlyList<CriteriaDomainView> _criteriaPanelItems = Array.Empty<CriteriaDomainView>();
    public IReadOnlyList<CriteriaDomainView> CriteriaPanelItems
    {
        get => _criteriaPanelItems;
        private set => SetProperty(ref _criteriaPanelItems, value);
    }

    private string _criteriaPanelStatus = "";
    public string CriteriaPanelStatus { get => _criteriaPanelStatus; set => SetProperty(ref _criteriaPanelStatus, value); }

    /// <summary>현재 과목 탭의 평가계획(영역·등급별 기준)을 패널용으로 재구성.</summary>
    private void RefreshCriteriaPanel()
    {
        var subjectName = SelectedSubject?.SubjectName;
        var plan = subjectName is null ? null : _plans.FirstOrDefault(p => p.SubjectName == subjectName);
        if (plan is null)
        {
            CriteriaPanelItems = Array.Empty<CriteriaDomainView>();
            CriteriaPanelStatus = subjectName is null
                ? "성적파일을 불러오면 표시됩니다."
                : $"'{subjectName}' 평가계획이 없습니다.\n[📝 명단·계획]에서 입력하거나 평가계획서를 불러오세요.";
            return;
        }

        var labels = _scales.Active.Levels.Select(l => l.Label).ToList();
        CriteriaPanelItems = plan.Domains.Select(domain =>
        {
            var levels = labels
                .Select(g => plan.Criteria.TryGetValue((domain, g), out var e)
                    ? new CriteriaLevelView(g, e.Text) : null)
                .Where(v => v is not null).Cast<CriteriaLevelView>().ToList();
            var ach = labels
                .Select(g => plan.Criteria.TryGetValue((domain, g), out var e) ? e.Achievement : null)
                .FirstOrDefault(a => !string.IsNullOrEmpty(a));
            return new CriteriaDomainView(domain, ach, levels);
        }).ToList();
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
        var win = new ScaleEditorWindow(new ScaleEditorViewModel(_scales))
        {
            Owner = Application.Current.MainWindow,
        };
        if (win.ShowDialog() == true)
        {
            OnPropertyChanged(nameof(ActiveScaleSummary));
            Log($"평가척도 적용: {ActiveScaleSummary}");
        }
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
                () => Subjects.Select(s => s.Sheet).ToList(),
                () => _plans,
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

    private string? _gradeFilePath;   // 편집 저장 대상

    private void LoadGrades(string path, bool silent = false)
    {
        if (!ConfirmSaveIfDirty()) return;   // 기존 편집 보호
        try
        {
            var sheets = WorkbookLoader.Load(path);
            if (sheets.Count == 0) throw new InvalidOperationException("번호/이름 컬럼이 있는 시트를 찾지 못했습니다.");

            Subjects.Clear();
            foreach (var s in sheets) Subjects.Add(new SubjectViewModel(this, s));
            SelectedSubject = Subjects.FirstOrDefault();   // 첫 과목 탭 자동 선택
            ExcelName = Path.GetFileName(path);
            _gradeFilePath = path;
            _appState.TouchGrade(path);
            OnPropertyChanged(nameof(RecentEntries));
            Log($"성적파일 로드: {ExcelName} ({string.Join(", ", sheets.Select(s => s.SubjectName))})");
        }
        catch (Exception ex)
        {
            if (silent) Log($"⚠ 최근 성적파일을 다시 열지 못했습니다: {ex.Message}");
            else ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 평가계획·명단이 바뀌면 열려 있는 성적표를 그에 맞춘다.
    /// 성적표가 없으면 새로 만들고, 있으면 학생 추가/삭제·영역 변경·새 과목을 반영하되
    /// 기존 학생의 성적·특기사항은 (번호,이름) 기준으로 보존한다.
    /// </summary>
    private void SyncGradeTableWithPlan()
    {
        if (Subjects.Count == 0) { EnsureGradeTableFromPlan(); return; }
        if (_roster.Count == 0 && _plans.Count == 0) return;

        bool changed = false;
        var planByName = _plans.ToDictionary(p => p.SubjectName);
        var currentNames = Subjects.Select(s => s.SubjectName).ToHashSet();
        var rebuilt = new List<SubjectViewModel>();

        foreach (var subj in Subjects)
        {
            var oldSheet = subj.Sheet;   // 미저장 편집 포함 현재 상태
            // 영역은 계획이 있으면 계획을 따르고, 없으면 기존 유지. 명단 변경은 모든 탭에 적용.
            var areas = planByName.TryGetValue(subj.SubjectName, out var plan) ? plan.Domains : oldSheet.Areas;
            var newSheet = BuildSheetFromRoster(subj.SubjectName, areas, oldSheet);
            if (SheetShapeEquals(oldSheet, newSheet)) { rebuilt.Add(subj); continue; }
            rebuilt.Add(new SubjectViewModel(this, newSheet));
            changed = true;
        }
        foreach (var plan in _plans.Where(p => !currentNames.Contains(p.SubjectName)))
        {
            rebuilt.Add(new SubjectViewModel(this, BuildSheetFromRoster(plan.SubjectName, plan.Domains, null)));
            changed = true;
        }
        if (!changed) return;

        var selected = SelectedSubject?.SubjectName;
        Subjects.Clear();
        foreach (var vm in rebuilt) Subjects.Add(vm);
        SelectedSubject = Subjects.FirstOrDefault(s => s.SubjectName == selected) ?? Subjects.FirstOrDefault();

        _gradeFilePath ??= Path.Combine(AppPaths.EnsureWorkspace(), "성적.xlsx");
        ExcelName = Path.GetFileName(_gradeFilePath);
        try
        {
            GradeWorkbookWriter.Write(_gradeFilePath, Subjects.Select(s => s.Sheet).ToList());
            _appState.TouchGrade(_gradeFilePath);
            OnPropertyChanged(nameof(RecentEntries));
            Log($"성적표를 명단·평가계획에 맞춰 갱신: {ExcelName} (기존 성적은 번호·이름 기준 보존)");
        }
        catch (Exception ex)
        {
            Log($"⚠ 성적표 갱신 저장 실패 ({ex.Message}) — 편집·종료 때 다시 시도합니다.");
        }
    }

    /// <summary>명단 순서대로 학생을 배치한 과목 시트 생성. 기존 시트가 있으면 성적·특기사항 이월.
    /// 명단이 비어 있으면 기존 학생을 유지한다 (영역 변경만 반영).</summary>
    private SubjectSheet BuildSheetFromRoster(string subjectName, IReadOnlyList<string> areas, SubjectSheet? old)
    {
        if (_roster.Count == 0)
            return new SubjectSheet(subjectName, areas, old?.Students ?? new List<Student>());

        var byKey = old?.Students.ToDictionary(s => (s.No, s.Name)) ?? new();
        var byName = new Dictionary<string, Student>();
        if (old is not null)
            foreach (var s in old.Students) byName[s.Name] = s;

        var students = _roster.Select(r =>
        {
            var prev = byKey.TryGetValue((r.No, r.Name), out var p1) ? p1
                     : byName.TryGetValue(r.Name, out var p2) ? p2 : null;   // 번호가 바뀐 학생도 이름으로 이월
            return new Student(r.No, r.Name,
                prev is null ? new Dictionary<string, string>() : new Dictionary<string, string>(prev.Grades),
                prev?.SpecialNote);
        }).ToList();

        return new SubjectSheet(subjectName, areas, students);
    }

    /// <summary>영역 구성과 학생(번호,이름) 목록이 같은지 — 같으면 표를 다시 만들지 않는다.</summary>
    private static bool SheetShapeEquals(SubjectSheet a, SubjectSheet b) =>
        a.Areas.SequenceEqual(b.Areas) &&
        a.Students.Select(s => (s.No, s.Name)).SequenceEqual(b.Students.Select(s => (s.No, s.Name)));

    /// <summary>
    /// 성적표가 안 열려 있는데 평가계획+명단이 준비되면, 성적표를 앱에서 바로 만들어
    /// 작업공간 성적.xlsx 로 저장한다 — 엑셀 양식을 따로 채우지 않아도 처음부터 앱만으로 진행 가능.
    /// 같은 파일이 이미 있으면 (지난 세션 성적 보호) 만들지 않고 그 파일을 연다.
    /// </summary>
    private void EnsureGradeTableFromPlan()
    {
        if (_plans.Count == 0 || _roster.Count == 0 || Subjects.Count > 0) return;

        var path = Path.Combine(AppPaths.EnsureWorkspace(), "성적.xlsx");
        if (File.Exists(path))
        {
            Log("작업공간에 기존 성적.xlsx 가 있어 그 파일을 엽니다. (새로 만들려면 파일을 옮기거나 지우세요)");
            LoadGrades(path, silent: true);
            if (Subjects.Count > 0) SyncGradeTableWithPlan();   // 파일이 현재 명단·계획과 다르면 맞춘다
            return;
        }

        var students = _roster
            .Select(r => new Student(r.No, r.Name, new Dictionary<string, string>(), null))
            .ToList();
        var sheets = _plans
            .Select(p => new SubjectSheet(p.SubjectName, p.Domains, students))
            .ToList();

        try
        {
            GradeWorkbookWriter.Write(path, sheets);
        }
        catch (Exception ex)
        {
            Log($"⚠ 성적표 파일 생성 실패: {ex.Message}");
            return;
        }

        Subjects.Clear();
        foreach (var s in sheets) Subjects.Add(new SubjectViewModel(this, s));
        SelectedSubject = Subjects.FirstOrDefault();
        _gradeFilePath = path;
        ExcelName = Path.GetFileName(path);
        _appState.TouchGrade(path);
        OnPropertyChanged(nameof(RecentEntries));
        Log($"평가계획·명단으로 성적표 생성: {ExcelName} ({sheets.Count}과목 × {students.Count}명) — 편집하면 자동 저장됩니다.");
    }

    // ── 자동 저장 ──────────────────────────────

    /// <summary>성적 표 편집 알림 (SubjectViewModel 에서 호출) — 디바운스 타이머 재시작.</summary>
    public void NotifyGradesEdited()
    {
        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    /// <summary>편집이 잦아들면 저장 대상 파일에 조용히 저장. 실패(파일 잠금 등)는 로그만 남기고 dirty 유지.</summary>
    private void AutoSaveGrades()
    {
        if (_gradeFilePath is null || !Subjects.Any(s => s.IsDirty)) return;
        try
        {
            GradeWorkbookWriter.Write(_gradeFilePath, Subjects.Select(s => s.Sheet).ToList());
            foreach (var s in Subjects) s.MarkSaved();
            Log($"자동 저장됨: {Path.GetFileName(_gradeFilePath)}");
        }
        catch (Exception ex)
        {
            Log($"⚠ 자동 저장 실패 ({ex.Message}) — 파일이 엑셀에서 열려 있으면 닫아 주세요. 다음 편집·종료 때 다시 시도합니다.");
        }
    }

    /// <summary>수정된 성적이 있으면 저장을 시도하고, 자동 저장이 불가능할 때만 묻는다. true=계속 진행, false=취소.</summary>
    public bool ConfirmSaveIfDirty()
    {
        if (!Subjects.Any(s => s.IsDirty)) return true;

        // 저장 대상 파일이 있으면 조용히 자동 저장 (평소 자동 저장과 같은 동작)
        if (_gradeFilePath is not null)
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

    private void SaveGrades()
    {
        try
        {
            var path = _gradeFilePath;
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
                _gradeFilePath = path;
                ExcelName = Path.GetFileName(path);
            }
            GradeWorkbookWriter.Write(path, Subjects.Select(s => s.Sheet).ToList());
            foreach (var s in Subjects) s.MarkSaved();
            Log($"성적 저장: {Path.GetFileName(path)}");
        }
        catch (Exception ex) { ShowError($"저장 실패: {ex.Message}"); }
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
        var sheets = Subjects.Select(s => s.Sheet)
            .Where(s => s.Students.Any(st => st.Grades.Count > 0)).ToList();
        if (sheets.Count == 0) { ShowError("입력할 성적이 없습니다. 성적표에 등급을 먼저 입력해 주세요."); return; }

        var ok = MessageBox.Show(
            $"전과목 자동 입력을 시작합니다.\n\n" +
            $"대상 과목({sheets.Count}개): {string.Join(", ", sheets.Select(s => s.SubjectName))}\n\n" +
            "★ 이 모드에서는 각 과목 입력 후 값 검증을 통과하면\n" +
            "   나이스 [저장]을 자동으로 누르고 다음 과목으로 넘어갑니다.\n" +
            "   (검증에 실패한 과목은 저장하지 않고 그 자리에서 중단합니다)\n\n" +
            "나이스 화면이 [교과별 평가] 조회 화면인지 확인한 뒤 계속하세요.",
            "전과목 자동 입력 — 과목별 자동 저장 동의", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        _cts = new CancellationTokenSource();
        var summary = new List<string>();
        try
        {
            for (int i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                Log(new string('─', 50));
                Log($"[전과목 {i + 1}/{sheets.Count}] '{sheet.SubjectName}' 과목으로 전환 중...");
                ProgressValue = 0;

                var (selOk, selWhy) = await _engine.SelectSubjectAsync(sheet.SubjectName, _cts.Token);
                if (!selOk)
                {
                    summary.Add($"✗ {sheet.SubjectName}: 과목 전환 실패 — {selWhy}");
                    break;   // 화면 상태를 모르는 채 계속 가지 않는다
                }

                var report = await _engine.RunSubjectAsync(
                    sheet, _scales.Active, dryRun: false, _progress, BuildResolveMatch(sheet), _cts.Token);

                if (report.Skipped.Any(s => s.Reason == "사용자 취소"))
                {
                    summary.Add($"· {sheet.SubjectName}: 사용자 취소 — 저장 안 함");
                    break;
                }
                if (report.Failed.Count > 0)
                {
                    summary.Add($"✗ {sheet.SubjectName}: 입력 실패 {report.Failed.Count}건 — 저장하지 않고 중단");
                    Log($"⚠ '{sheet.SubjectName}' 검증 실패로 저장하지 않았습니다. 나이스에서 값을 확인하세요.");
                    break;
                }
                if (report.Done.Count == 0)
                {
                    summary.Add($"· {sheet.SubjectName}: 입력할 값 없음 — 저장 생략");
                    continue;
                }

                Log($"[{sheet.SubjectName}] 검증 통과 ({report.Done.Count}건) → 저장 중...");
                var (saveOk, saveWhy) = await _engine.SaveScreenAsync(_cts.Token);
                if (!saveOk)
                {
                    summary.Add($"✗ {sheet.SubjectName}: 입력 {report.Done.Count}건 완료했으나 저장 실패({saveWhy}) — 중단. " +
                                "나이스에서 직접 [저장]을 눌러주세요.");
                    break;
                }
                summary.Add($"✓ {sheet.SubjectName}: {report.Done.Count}건 입력·저장" +
                            (report.Skipped.Count > 0 ? $" (건너뜀 {report.Skipped.Count})" : ""));
            }
        }
        catch (OperationCanceledException) { summary.Add("⛔ 사용자 중지"); }
        catch (Exception ex)
        {
            summary.Add($"✗ 오류: {ex.Message}");
            Log($"전과목 입력 오류: {ex.Message}");
        }

        Log(new string('=', 50));
        Log("전과목 자동 입력 결과:");
        foreach (var s in summary) Log("  " + s);
        MessageBox.Show("전과목 자동 입력 결과\n\n" + string.Join("\n", summary),
            "전과목 입력 완료", MessageBoxButton.OK,
            summary.Any(s => s.StartsWith("✗")) ? MessageBoxImage.Warning : MessageBoxImage.Information);
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
