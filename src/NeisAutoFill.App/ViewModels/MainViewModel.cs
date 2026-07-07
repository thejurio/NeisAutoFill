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
    private readonly IProgress<ProgressInfo> _progress;
    private CancellationTokenSource? _cts;
    private GeneratorViewModel? _generatorVm;   // 생성 결과 보존을 위해 단일 인스턴스 유지

    private readonly Automation.EngineOptions _engineOptions;

    public MainViewModel(INeisEngine engine, IScaleStore scales,
        GeneratorSettingsStore generatorSettings, NarrativeStore narratives,
        Automation.EngineOptions engineOptions)
    {
        _engine = engine;
        _scales = scales;
        _generatorSettings = generatorSettings;
        _narratives = narratives;
        _engineOptions = engineOptions;

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

        _ = AutoConnectLoopAsync();   // 앱 시작부터 자동 연결·재연결 (이미 열린 브라우저도 자동 포착)
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

    public IReadOnlyList<SubjectPlan> Plans => _plans;

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

    private void LoadPlan(string path)
    {
        try
        {
            _plans = PlanWorkbookLoader.Load(path, _scales.Active);
            _roster = PlanWorkbookLoader.LoadRoster(path);
            PlanName = Path.GetFileName(path);
            Log($"평가계획서 로드: {PlanName} " +
                $"({string.Join(", ", _plans.Select(p => $"{p.SubjectName} {p.Domains.Count}영역"))}" +
                (_roster.Count > 0 ? $" / 명단 {_roster.Count}명)" : ")"));
        }
        catch (Exception ex) { ShowError($"평가계획서 오류: {ex.Message}"); }
    }

    private void SaveStep1Template()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Excel|*.xlsx",
            FileName = "평가계획서_양식.xlsx",
            Title = "평가계획서 양식 저장",
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

    /// <summary>현재 활성 척도 요약 (예: "잘함/보통/노력요함").</summary>
    public string ActiveScaleSummary =>
        string.Join("/", _scales.Active.Levels.Select(l => l.Label));

    /// <summary>성적 표 드롭다운 편집용 등급 라벨 (빈칸 선택 허용).</summary>
    public IReadOnlyList<string> GradeLabels =>
        new[] { "" }.Concat(_scales.Active.Levels.Select(l => l.Label)).ToList();

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
                _scales, _generatorSettings, _narratives, _engine, Log);
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

    private void LoadGrades(string path)
    {
        if (!ConfirmSaveIfDirty()) return;   // 기존 편집 보호
        try
        {
            var sheets = WorkbookLoader.Load(path);
            if (sheets.Count == 0) throw new InvalidOperationException("번호/이름 컬럼이 있는 시트를 찾지 못했습니다.");

            Subjects.Clear();
            foreach (var s in sheets) Subjects.Add(new SubjectViewModel(this, s));
            ExcelName = Path.GetFileName(path);
            _gradeFilePath = path;
            Log($"성적파일 로드: {ExcelName} ({string.Join(", ", sheets.Select(s => s.SubjectName))})");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>수정된 성적이 있으면 저장 여부를 묻는다. true=계속 진행, false=취소.</summary>
    public bool ConfirmSaveIfDirty()
    {
        if (!Subjects.Any(s => s.IsDirty)) return true;

        var r = MessageBox.Show(
            "수정한 성적이 있습니다. 엑셀 파일에 저장할까요?",
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
                var dlg = new SaveFileDialog { Filter = "Excel|*.xlsx", FileName = "성적.xlsx" };
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
            Func<string, Task<bool>> confirmOrder = msg =>
                Application.Current.Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(msg, "순서 기반 입력 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    == MessageBoxResult.Yes).Task;

            var report = await _engine.RunSubjectAsync(sheet, _scales.Active, dryRun, _progress, confirmOrder, _cts.Token);
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
