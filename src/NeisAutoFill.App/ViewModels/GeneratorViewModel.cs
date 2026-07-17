using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.App.Services;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.ViewModels;

/// <summary>AI 서술문 생성기 화면. DooEval 2단계 대시보드의 C# 판.</summary>
public sealed class GeneratorViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly Func<IReadOnlyList<SubjectSheet>> _getSheets;   // 메인에서 로드된 성적파일
    private readonly Func<IReadOnlyList<SubjectPlan>> _getPlans;     // 메인에서 로드된 평가계획서
    private readonly IScaleStore _scales;
    private readonly GeneratorSettingsStore _settings;
    private readonly NarrativeStore _store;                          // 생성 결과 영속화
    private readonly Automation.Abstractions.INeisEngine _engine;
    private readonly Action<string> _mainLog;                        // 메인 창 로그로도 남김
    private IReadOnlyList<SubjectPlan> _plans = Array.Empty<SubjectPlan>();

    // 과목별 생성 결과 보존 (과목 전환·창 재오픈에도 유지)
    private readonly Dictionary<string, List<StudentGenItem>> _itemsBySubject = new();

    public GeneratorViewModel(
        Func<IReadOnlyList<SubjectSheet>> getSheets,
        Func<IReadOnlyList<SubjectPlan>> getPlans,
        IScaleStore scales,
        GeneratorSettingsStore settings,
        NarrativeStore store,
        Automation.Abstractions.INeisEngine engine,
        Action<string> mainLog)
    {
        _getSheets = getSheets;
        _getPlans = getPlans;
        _scales = scales;
        _settings = settings;
        _store = store;
        _engine = engine;
        _mainLog = mainLog;

        GenerateAllCommand = new AsyncRelayCommand(() => GenerateAsync(onlySelected: false));
        GenerateSelectedCommand = new AsyncRelayCommand(() => GenerateAsync(onlySelected: true));
        UploadCommand = new AsyncRelayCommand(() => UploadAsync(dryRun: false));
        ExportCommand = new RelayCommand(ExportToExcel);
        DeleteAllCommand = new RelayCommand(() => Delete(onlySelected: false));
        DeleteSelectedCommand = new RelayCommand(() => Delete(onlySelected: true));

        RefreshSubjects();
    }

    /// <summary>생성된 서술문 삭제 (결과 비우기 + 저장소에서 제거).</summary>
    private void Delete(bool onlySelected)
    {
        var targets = Students.Where(s => (!onlySelected || s.IsSelected) && s.HasValidResult).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show(
                onlySelected ? "선택된 학생 중 삭제할 서술문이 없습니다." : "삭제할 서술문이 없습니다.",
                "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(
            $"'{SelectedSubject}' {targets.Count}명의 생성된 서술문을 삭제합니다.\n(나이스에 이미 입력된 내용은 지워지지 않습니다)",
            "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        foreach (var item in targets)
        {
            item.Result = "";        // setter 가 저장소에서 제거까지 처리
            item.UploadState = "";
        }
    }

    public ObservableCollection<string> SubjectNames { get; } = new();
    public ObservableCollection<StudentGenItem> Students { get; } = new();

    private string? _selectedSubject;
    public string? SelectedSubject
    {
        get => _selectedSubject;
        set { if (SetProperty(ref _selectedSubject, value)) RebuildStudents(); }
    }

    private string _planStatus = "(평가계획서 미로드 — 기준내용 없이 등급만으로 생성됩니다)";
    public string PlanStatus { get => _planStatus; set => SetProperty(ref _planStatus, value); }

    // ── 설정 바인딩 ────────────────────────
    // 서버(GAS) URL 은 UI 에서 제거 — 변경이 필요하면 %AppData%\NeisAutoFill\settings.json 에서 수정
    public string MaxNarrativeBytes
    {
        get => _settings.Options.MaxNarrativeBytes.ToString();
        set
        {
            if (int.TryParse(value, out var n) && n >= 0)
            {
                _settings.Options = _settings.Options with { MaxNarrativeBytes = n };
                _settings.Save();
            }
            OnPropertyChanged();
        }
    }

    public ICommand GenerateAllCommand { get; }
    public ICommand GenerateSelectedCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand DeleteSelectedCommand { get; }

    /// <summary>모든 과목의 생성된 서술문을 xlsx 로 저장 (과목별 시트).</summary>
    private void ExportToExcel()
    {
        var data = new Dictionary<string, IReadOnlyList<(string, string, string)>>();
        foreach (var sheet in _getSheets())
        {
            var rows = new List<(string, string, string)>();
            var cached = _itemsBySubject.TryGetValue(sheet.SubjectName, out var items)
                ? items.ToDictionary(i => (i.No, i.Name))
                : new Dictionary<(string, string), StudentGenItem>();
            foreach (var st in sheet.Students)
            {
                // 화면 캐시 우선, 없으면 저장소(narratives.json)에서
                var text = cached.TryGetValue((st.No, st.Name), out var item) && item.HasValidResult
                    ? item.Result
                    : _store.Get(sheet.SubjectName, st.No, st.Name);
                if (!string.IsNullOrWhiteSpace(text))
                    rows.Add((st.No, st.Name, text!.Trim()));
            }
            if (rows.Count > 0) data[sheet.SubjectName] = rows;
        }

        if (data.Count == 0)
        {
            MessageBox.Show("저장할 서술문이 없습니다. 먼저 생성해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Excel|*.xlsx",
            FileName = $"교과서술문_{DateTime.Now:yyyyMMdd}.xlsx",
            Title = "서술문 엑셀 저장",
            InitialDirectory = AppPaths.EnsureWorkspace(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Excel.NarrativeWorkbookWriter.Write(dlg.FileName, data);
            _mainLog($"서술문 엑셀 저장: {dlg.FileName} ({string.Join(", ", data.Keys)})");
            MessageBox.Show($"저장했습니다.\n{dlg.FileName}", "완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "저장 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool _selectAll;
    /// <summary>전체선택 토글 — 모든 학생의 체크 상태를 일괄 변경.</summary>
    public bool SelectAll
    {
        get => _selectAll;
        set
        {
            if (SetProperty(ref _selectAll, value))
                foreach (var s in Students) s.IsSelected = value;
        }
    }

    // ── 동작 ──────────────────────────────
    /// <summary>메인 창에서 로드된 성적·평가계획을 다시 읽어 반영 (창 열 때마다 호출).</summary>
    public void RefreshSubjects()
    {
        _plans = _getPlans();
        PlanStatus = _plans.Count > 0
            ? $"평가계획: {string.Join(", ", _plans.Select(p => $"{p.SubjectName} {p.Domains.Count}영역"))}"
            : "(평가계획서 미로드 — 메인 화면 [평가계획서 열기]로 불러오면 평가기준이 서술문에 반영됩니다)";

        SubjectNames.Clear();
        foreach (var s in _getSheets()) SubjectNames.Add(s.SubjectName);
        if (SelectedSubject is null || !SubjectNames.Contains(SelectedSubject))
            SelectedSubject = SubjectNames.FirstOrDefault();
        else
            RebuildStudents();   // 같은 과목 유지 시에도 데이터 변경 반영
    }

    private void RebuildStudents()
    {
        Students.Clear();
        var sheet = _getSheets().FirstOrDefault(s => s.SubjectName == SelectedSubject);
        if (sheet is null) return;

        var plan = _plans.FirstOrDefault(p => p.SubjectName == sheet.SubjectName);

        // 이전 생성 결과를 (번호, 이름) 으로 이월 — 과목 전환·재오픈에도 서술문 유지
        var previous = _itemsBySubject.TryGetValue(sheet.SubjectName, out var old)
            ? old.ToDictionary(i => (i.No, i.Name))
            : new Dictionary<(string, string), StudentGenItem>();

        var fresh = new List<StudentGenItem>();
        foreach (var st in sheet.Students)
        {
            var domains = EvaluationAssembler.BuildDomainPoints(st, sheet.Areas, plan, _scales.Active);
            if (domains.Count == 0) continue;   // 등급이 하나도 없는 학생은 제외 (DooEval 동작과 동일)
            var item = new StudentGenItem(this, sheet.SubjectName, st, domains);
            if (previous.TryGetValue((st.No, st.Name), out var prev))
            {
                item.RestoreResult(prev.Result);
                item.UploadState = prev.UploadState;
                item.IsSelected = prev.IsSelected;
            }
            else if (_store.Get(sheet.SubjectName, st.No, st.Name) is { } saved)
            {
                item.RestoreResult(saved);      // 지난 실행에서 저장된 서술문 복원
            }
            fresh.Add(item);
        }
        _itemsBySubject[sheet.SubjectName] = fresh;
        foreach (var i in fresh) Students.Add(i);
    }

    /// <summary>생성 결과 영속화 — 생성 완료·수동 편집 시 호출됨.</summary>
    internal void PersistResult(StudentGenItem item)
    {
        if (SelectedSubject is null) return;
        _store.Set(SelectedSubject, item.No, item.Name,
            item.HasValidResult ? item.Result : "");
    }

    private async Task GenerateAsync(bool onlySelected)
    {
        var targets = Students.Where(s => !onlySelected || s.IsSelected).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("선택된 학생이 없습니다. 학생 왼쪽 체크박스를 선택해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var item in targets)
            await item.GenerateAsync();   // 순차 실행 — API 부하·순서 보존 (DooEval 은 병렬이었으나 안정성 우선)
    }

    /// <summary>생성 완료된 서술문을 나이스 화면에 입력 (Phase 8). 저장은 수동.</summary>
    private async Task UploadAsync(bool dryRun)
    {
        if (SelectedSubject is null) return;
        if (!_engine.Connected)
        {
            MessageBox.Show("나이스에 아직 연결되지 않았습니다. 메인 화면에서 [① NEIS 접속] 후 로그인·조회하면 자동으로 연결됩니다.",
                "연결 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var items = Students.Where(s => s.HasValidResult).ToList();
        if (items.Count == 0)
        {
            MessageBox.Show("입력할 서술문이 없습니다. 먼저 생성해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!dryRun)
        {
            var ok = MessageBox.Show(
                $"'{SelectedSubject}' {items.Count}명의 서술문을 나이스 화면에 입력합니다.\n" +
                "(저장은 하지 않으며, 확인 후 나이스에서 직접 [저장]을 누르세요)\n\n" +
                "나이스 화면이 해당 과목의 종합의견 입력 화면인지 확인하셨나요?",
                "확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ok != MessageBoxResult.Yes) return;
        }

        var entries = items.Select(s => new NarrativeEntry(s.No, s.Name, s.Result.Trim())).ToList();
        foreach (var s in items) s.UploadState = "";

        var progress = new Progress<Automation.Abstractions.ProgressInfo>(p =>
        {
            if (!string.IsNullOrEmpty(p.Message)) _mainLog(p.Message);
        });

        try
        {
            var report = await _engine.RunNarrativesAsync(
                SelectedSubject, entries, dryRun,
                _settings.Options.MaxNarrativeBytes, progress);

            foreach (var s in items)
            {
                if (report.Done.Any(d => d.No == s.No && d.Name == s.Name))
                    s.UploadState = dryRun ? "☑ 검증 통과" : "✓ 입력됨 (미저장)";
                else if (report.Failed.FirstOrDefault(f => f.No == s.No && f.Name == s.Name) is { } f)
                    s.UploadState = $"✗ {f.Reason}";
                else if (report.Skipped.FirstOrDefault(k => k.No == s.No && k.Name == s.Name) is { } k)
                    s.UploadState = $"⚠ {k.Reason}";
            }

            var summary = $"[{SelectedSubject}] 서술문 {(dryRun ? "검증" : "입력")} 완료: " +
                          $"성공 {report.Done.Count} / 건너뜀 {report.Skipped.Count} / 실패 {report.Failed.Count}";
            _mainLog(summary);
            if (!dryRun) _mainLog("※ 저장하지 않았습니다. 나이스에서 값 확인 후 [저장]을 눌러주세요.");
            MessageBox.Show(summary, "완료", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _mainLog($"서술문 입력 오류: {ex.Message}");
            MessageBox.Show(ex.Message, "서술문 입력 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    internal IEvaluationGenerator CreateGenerator() =>
        new GasBackendGenerator(Http, _settings.Options);

    internal GradeScale ActiveScale => _scales.Active;
}

/// <summary>학생 한 명의 생성 항목 (행).</summary>
public sealed class StudentGenItem : ObservableObject
{
    private readonly GeneratorViewModel _parent;
    private readonly string _subject;
    private readonly Student _student;
    private readonly IReadOnlyList<DomainPoint> _domains;

    public StudentGenItem(GeneratorViewModel parent, string subject,
                          Student student, IReadOnlyList<DomainPoint> domains)
    {
        _parent = parent;
        _subject = subject;
        _student = student;
        _domains = domains;
        GenerateCommand = new AsyncRelayCommand(GenerateAsync);
    }

    public string No => _student.No;
    public string Name => _student.Name;
    public string DomainsSummary =>
        string.Join("  ", _domains.Select(d => $"{d.DomainName}:{d.Grade}"));
    public string NoteSummary => _student.SpecialNote ?? "(특기사항 없음)";

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    private string _result = "";
    public string Result
    {
        get => _result;
        set
        {
            if (SetProperty(ref _result, value) && !Busy)
                _parent.PersistResult(this);   // 수동 편집도 즉시 저장 (생성 중 중간값은 제외)
        }
    }

    /// <summary>저장된 서술문 복원 — 저장 트리거 없이 값만 세팅.</summary>
    public void RestoreResult(string text)
    {
        _result = text;
        OnPropertyChanged(nameof(Result));
    }

    private string _uploadState = "";
    public string UploadState { get => _uploadState; set => SetProperty(ref _uploadState, value); }

    /// <summary>나이스 입력 대상이 될 수 있는 유효한 생성 결과인지.</summary>
    public bool HasValidResult =>
        !string.IsNullOrWhiteSpace(Result) &&
        !Result.StartsWith("[오류]") && !Result.StartsWith("⏳");

    private bool _busy;
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public ICommand GenerateCommand { get; }

    public async Task GenerateAsync()
    {
        Busy = true;
        Result = "⏳ 생성 중...";
        try
        {
            var gen = _parent.CreateGenerator();
            Result = await gen.GenerateAsync(
                _student.Name, _subject, _domains, _student.SpecialNote, _parent.ActiveScale);
        }
        catch (Exception ex)
        {
            Result = $"[오류] {ex.Message}";
        }
        finally
        {
            Busy = false;
            _parent.PersistResult(this);   // 생성 완료(성공/오류) 후 저장
        }
    }
}
