using System.Collections.ObjectModel;
using System.IO;
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

/// <summary>AI 서술문 생성기 화면. 생성은 GenerationQueue 로 백그라운드 실행.</summary>
public sealed class GeneratorViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<SubjectSheet>> _getSheets;   // 메인에서 로드된 성적파일
    private readonly Func<IReadOnlyList<SubjectPlan>> _getPlans;     // 메인에서 로드된 평가계획서
    private readonly IScaleStore _scales;
    private readonly GeneratorSettingsStore _settings;
    private readonly NarrativeStore _store;                          // 생성 결과 영속화
    private readonly GenerationQueue _queue;                         // 백그라운드 생성
    private readonly NarrativeMirror _mirror;                        // 서술문.xlsx 저장
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
        GenerationQueue queue,
        NarrativeMirror mirror,
        Automation.Abstractions.INeisEngine engine,
        Action<string> mainLog)
    {
        _getSheets = getSheets;
        _getPlans = getPlans;
        _scales = scales;
        _settings = settings;
        _store = store;
        _queue = queue;
        _mirror = mirror;
        _engine = engine;
        _mainLog = mainLog;

        _queue.JobStarted += job => FindItem(job)?.RestoreResult("⏳ 생성 중...");
        _queue.JobFinished += OnJobFinished;
        _queue.StateChanged += () =>
        {
            OnPropertyChanged(nameof(QueueStatus));
            OnPropertyChanged(nameof(IsGenerating));
            OnPropertyChanged(nameof(QueueProgress));
            OnPropertyChanged(nameof(QueueTotal));
            if (!_queue.IsBusy) RefreshQuality();   // 배치가 끝나 idle 이 되면 품질 점검
        };

        GenerateAllCommand = new RelayCommand(() => EnqueueVisible(onlySelected: false));
        GenerateSelectedCommand = new RelayCommand(() => EnqueueVisible(onlySelected: true));
        GenerateSubjectsCommand = new RelayCommand(GenerateSubjects);
        CancelGenerationCommand = new RelayCommand(() => _queue.CancelAll());
        UploadCommand = new AsyncRelayCommand(() => UploadAsync(dryRun: false));
        UploadAllCommand = new AsyncRelayCommand(UploadAllAsync);
        SaveCommand = new RelayCommand(() =>
        {
            if (_mirror.SaveNow())
                MessageBox.Show($"저장했습니다.\n{NarrativeMirror.MirrorPath}", "완료",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        });
        ExportCommand = new RelayCommand(ExportToExcel);
        ImportCommand = new RelayCommand(ImportFromExcel);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected);
        DeleteSubjectCommand = new RelayCommand(DeleteSubject);
        DeleteAllSubjectsCommand = new RelayCommand(DeleteAllSubjects);

        RefreshSubjects();
    }

    // ── 백그라운드 생성 ────────────────────

    public string QueueStatus => _queue.Status;
    public bool IsGenerating => _queue.IsBusy;
    public int QueueProgress => _queue.Done + _queue.Failed;
    public int QueueTotal => Math.Max(_queue.Total, 1);

    /// <summary>현재 화면에 보이는 항목 중 job 과 같은 학생 (다른 과목 화면이면 null).</summary>
    private StudentGenItem? FindItem(GenJob job) =>
        job.Subject == SelectedSubject
            ? Students.FirstOrDefault(s => s.No == job.No && s.Name == job.Name)
            : null;

    /// <summary>큐 완료 콜백 — 화면에 보이는 항목이면 결과 반영 (store 저장은 큐가 이미 함).</summary>
    private void OnJobFinished(GenJob job, string text, bool ok) =>
        FindItem(job)?.RestoreResult(ok ? text : $"[오류] {text}");

    /// <summary>현재 과목의 (선택된) 학생들을 생성 큐에 넣는다.</summary>
    private void EnqueueVisible(bool onlySelected)
    {
        var targets = Students.Where(s => !onlySelected || s.IsSelected).ToList();
        if (targets.Count == 0)
        {
            MessageBox.Show("선택된 학생이 없습니다. 학생 왼쪽 체크박스를 선택해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        foreach (var item in targets) item.RestoreResult("⏳ 대기 중...");
        _queue.Enqueue(targets.Select(t => t.ToJob(_scales.Active)));
        _mainLog($"[{SelectedSubject}] 서술문 {targets.Count}건 생성 시작 (백그라운드)");
    }

    /// <summary>항목 한 건 생성 (행의 [생성] 버튼).</summary>
    internal void EnqueueSingle(StudentGenItem item)
    {
        item.RestoreResult("⏳ 대기 중...");
        _queue.Enqueue(new[] { item.ToJob(_scales.Active) });
    }

    /// <summary>과목 선택 대화상자를 띄워 선택된 과목 전체를 큐에 넣는다 (전과목 일괄 생성).</summary>
    private void GenerateSubjects()
    {
        var sheets = _getSheets();
        if (sheets.Count == 0)
        {
            MessageBox.Show("성적 자료가 없습니다. 메인 화면에서 성적을 먼저 준비해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picks = sheets.Select(s => new SubjectPick(s.SubjectName, CountEligible(s))).ToList();
        var win = new BatchGenerateWindow(picks) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        var chosen = picks.Where(p => p.IsChecked).Select(p => p.Name).ToHashSet();
        var jobs = sheets.Where(s => chosen.Contains(s.SubjectName)).SelectMany(JobsForSheet).ToList();
        if (jobs.Count == 0)
        {
            MessageBox.Show("선택한 과목에 생성할 학생(등급 입력된)이 없습니다.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 화면에 보이는 과목이면 대기 표시
        foreach (var item in Students)
            if (chosen.Contains(SelectedSubject ?? "") && jobs.Any(job => job.Subject == SelectedSubject && job.No == item.No && job.Name == item.Name))
                item.RestoreResult("⏳ 대기 중...");

        _queue.Enqueue(jobs);
        _mainLog($"전과목 서술문 생성 시작: {string.Join(", ", chosen)} — 총 {jobs.Count}건 (백그라운드)");
    }

    private int CountEligible(SubjectSheet sheet) => JobsForSheet(sheet).Count();

    private IEnumerable<GenJob> JobsForSheet(SubjectSheet sheet)
    {
        var plan = _plans.FirstOrDefault(p => p.SubjectName == sheet.SubjectName);
        var scale = _scales.Active;
        foreach (var st in sheet.Students)
        {
            var domains = CapDomains(EvaluationAssembler.BuildDomainPoints(st, sheet.Areas, plan, scale));
            if (domains.Count > 0)
                yield return new GenJob(sheet.SubjectName, st.No, st.Name, domains, st.SpecialNote, scale);
        }
    }

    /// <summary>⚙ 설정의 '최대 영역 수' 적용 — 초과하면 무작위로 N개 선정 (0 = 전체).
    /// 선정된 영역은 원래 순서를 유지해 서술문 흐름이 자연스럽게.</summary>
    private IReadOnlyList<DomainPoint> CapDomains(IReadOnlyList<DomainPoint> domains)
    {
        int max = _settings.Options.MaxDomains;
        if (max <= 0 || domains.Count <= max) return domains;
        return Enumerable.Range(0, domains.Count)
            .OrderBy(_ => Random.Shared.Next()).Take(max)   // 무작위 N개
            .OrderBy(i => i)                                 // 원래 순서 복원
            .Select(i => domains[i]).ToList();
    }

    /// <summary>서술문 엑셀(과목별 시트)을 읽어 저장소에 반영 — 엑셀에서 직접 고친 내용 되가져오기.</summary>
    private void ImportFromExcel()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel|*.xlsx",
            Title = "서술문 엑셀 불러오기",
            InitialDirectory = AppPaths.EnsureWorkspace(),
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var data = Excel.NarrativeWorkbookLoader.Load(dlg.FileName);
            int count = 0;
            foreach (var (subject, rows) in data)
                foreach (var (no, name, text) in rows)
                {
                    _store.Set(subject, no, name, text);
                    count++;
                }
            RebuildStudents();   // 화면 갱신
            _mainLog($"서술문 엑셀 불러오기: {Path.GetFileName(dlg.FileName)} — {data.Count}과목 {count}건");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "불러오기 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>현재 과목 화면에서 선택된 학생의 서술문만 삭제.</summary>
    private void DeleteSelected()
    {
        var targets = Students.Where(s => s.IsSelected && s.HasValidResult).ToList();
        if (Confirm(targets.Count, $"선택한 {targets.Count}명의 서술문", "선택된 학생 중 삭제할 서술문이 없습니다."))
            ClearItems(targets);
    }

    /// <summary>현재 과목의 모든 서술문 삭제.</summary>
    private void DeleteSubject()
    {
        var targets = Students.Where(s => s.HasValidResult).ToList();
        if (Confirm(targets.Count, $"'{SelectedSubject}' 과목의 서술문 {targets.Count}건", "이 과목에 삭제할 서술문이 없습니다."))
            ClearItems(targets);
    }

    /// <summary>모든 과목의 서술문 삭제 (저장소에서도 제거).</summary>
    private void DeleteAllSubjects()
    {
        var total = _store.All().Count;
        if (total == 0) { Info("삭제할 서술문이 없습니다."); return; }
        if (!Confirm(total, $"전체 과목의 서술문 {total}건", "")) return;

        foreach (var (subject, no, name, _) in _store.All().ToList())
            _store.Set(subject, no, name, "");   // 저장소에서 제거
        RebuildStudents();                        // 화면 반영
    }

    private static bool Confirm(int count, string what, string emptyMsg)
    {
        if (count == 0) { if (emptyMsg != "") Info(emptyMsg); return false; }
        return MessageBox.Show(
            $"{what}을 삭제합니다.\n(나이스에 이미 입력된 내용은 지워지지 않습니다)",
            "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static void ClearItems(IEnumerable<StudentGenItem> items)
    {
        foreach (var item in items)
        {
            item.Result = "";        // setter 가 저장소에서 제거까지 처리
            item.UploadState = "";
        }
    }

    private static void Info(string msg) =>
        MessageBox.Show(msg, "안내", MessageBoxButton.OK, MessageBoxImage.Information);

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

    public ICommand GenerateAllCommand { get; }
    public ICommand GenerateSelectedCommand { get; }
    public ICommand GenerateSubjectsCommand { get; }
    public ICommand CancelGenerationCommand { get; }
    public ICommand UploadCommand { get; }
    public ICommand UploadAllCommand { get; }
    public ICommand SaveCommand { get; }

    /// <summary>과목별 (번호,이름,서술문) — 화면 캐시 우선, 없으면 저장소. 서술문 있는 과목만.</summary>
    private Dictionary<string, List<NarrativeEntry>> CollectNarratives()
    {
        var result = new Dictionary<string, List<NarrativeEntry>>();
        foreach (var sheet in _getSheets())
        {
            var cached = _itemsBySubject.TryGetValue(sheet.SubjectName, out var items)
                ? items.ToDictionary(i => (i.No, i.Name))
                : new Dictionary<(string, string), StudentGenItem>();
            var rows = new List<NarrativeEntry>();
            foreach (var st in sheet.Students)
            {
                var text = cached.TryGetValue((st.No, st.Name), out var item) && item.HasValidResult
                    ? item.Result
                    : _store.Get(sheet.SubjectName, st.No, st.Name);
                if (!string.IsNullOrWhiteSpace(text))
                    rows.Add(new NarrativeEntry(st.No, st.Name, text!.Trim()));
            }
            if (rows.Count > 0) result[sheet.SubjectName] = rows;
        }
        return result;
    }

    /// <summary>전과목 서술문 자동 입력 (Phase 5.5, A안: 과목별 검증 통과 시 자동 저장).</summary>
    private async Task UploadAllAsync()
    {
        if (!_engine.Connected)
        {
            MessageBox.Show("나이스에 아직 연결되지 않았습니다. 메인 화면에서 [① NEIS 접속] 후 로그인·조회하면 자동으로 연결됩니다.",
                "연결 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var allBySubject = CollectNarratives();
        if (allBySubject.Count == 0)
        {
            MessageBox.Show("입력할 서술문이 없습니다. 먼저 생성해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 과목 체크리스트 — 입력할 과목을 고르고, 자동 저장 동의도 이 창에서
        var picks = allBySubject
            .Select(kv => new SubjectPick(kv.Key, kv.Value.Count, $"서술문 {kv.Value.Count}명 입력 예정"))
            .ToList();
        var win = new BatchGenerateWindow(picks,
            title: "전과목 서술문 입력",
            description: "나이스에 입력할 과목을 선택하세요. 나이스 화면이 [학기말 종합의견] 조회 화면인지 확인한 뒤 시작하세요.",
            startLabel: "🚀 입력 시작",
            warning: "각 과목 입력 후 검증을 통과하면 나이스 [저장]을 자동으로 누르고 다음 과목으로 넘어갑니다. " +
                     "검증에 실패한 과목은 저장하지 않고 그 자리에서 중단합니다.")
        { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        var chosen = picks.Where(p => p.IsChecked).Select(p => p.Name).ToHashSet();
        var bySubject = allBySubject.Where(kv => chosen.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (bySubject.Count == 0) return;

        var progress = new Progress<Automation.Abstractions.ProgressInfo>(p =>
        {
            if (!string.IsNullOrEmpty(p.Message)) _mainLog(p.Message);
        });

        // runSubject 를 델리게이트로 빼 재시도 때 그대로 재사용
        Func<string, Task<Automation.BatchUploadRunner.SubjectResult>> runSubject = async subject =>
        {
            var report = await _engine.RunNarrativesAsync(
                subject, bySubject[subject], dryRun: false, _settings.Options.MaxNarrativeBytes, progress);
            return new Automation.BatchUploadRunner.SubjectResult(
                report.Done.Count, report.Failed, report.Skipped.Count, UserCancelled: false);
        };

        var outcomes = await Automation.BatchUploadRunner.RunAsync(
            bySubject.Keys.ToList(), _engine, runSubject, _mainLog, unit: "명", CancellationToken.None);

        _mainLog("전과목 서술문 입력 결과: " +
            string.Join(" / ", Automation.BatchUploadRunner.Summarize(outcomes)));

        BatchResultWindow.ShowResult(outcomes, "명",
            retry: async subs =>
                await Automation.BatchUploadRunner.RunAsync(subs, _engine, runSubject, _mainLog, "명", CancellationToken.None),
            owner: Application.Current.MainWindow);
    }
    public ICommand ExportCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand DeleteSubjectCommand { get; }
    public ICommand DeleteAllSubjectsCommand { get; }

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
            : "(평가계획 없음 — 메인 [📁 자료 준비]에서 입력하거나 불러오면 평가기준이 서술문에 반영됩니다)";

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
            var domains = CapDomains(EvaluationAssembler.BuildDomainPoints(st, sheet.Areas, plan, _scales.Active));
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
        RefreshQuality();
    }

    // ── 서술문 품질 점검 (F6) ──────────────
    /// <summary>설정된 바이트 제한 (0=검사 안 함). 학생 항목이 참조.</summary>
    internal int MaxNarrativeBytes => _settings.Options.MaxNarrativeBytes;
    /// <summary>품질 점검(바이트·복붙 경고) 표시 여부 — 설정에서 켜고 끔.</summary>
    internal bool ShowQuality => _settings.Options.ShowNarrativeQuality;

    /// <summary>"다르게 다시 생성" 시 AI 에 붙이는 추가 지시 (표현만 달리, 없는 사실 금지).</summary>
    private const string DistinctHint =
        "이 학생의 서술문이 다른 학생과 표현이 매우 비슷하게 작성되었습니다. " +
        "같은 성취 수준이라도 문장 구조·표현·어휘를 충분히 달리하여 다른 학생과 겹치지 않게 새로 작성하세요. " +
        "단, 없는 사실을 지어내지 말고 제공된 성취 내역 범위 안에서만 표현을 다르게 하세요.";

    private string _qualityNote = "";
    /// <summary>학생 간 복붙 의심 요약 (비면 문제 없음).</summary>
    public string QualityNote { get => _qualityNote; private set => SetProperty(ref _qualityNote, value); }

    /// <summary>현재 과목 학생들의 서술문을 점검 — 매우 유사한(복붙 의심) 항목을 표시. 설정이 꺼져 있으면 아무것도 안 함.</summary>
    public void RefreshQuality()
    {
        foreach (var s in Students) s.IsSimilarSuspect = false;
        if (!ShowQuality) { QualityNote = ""; return; }

        var items = Students.Where(s => s.HasValidResult).ToList();
        if (items.Count < 2) { QualityNote = ""; return; }

        var groups = NarrativeQuality.SimilarGroups(items.Select(s => s.Result).ToList());
        foreach (var g in groups)
            foreach (var idx in g)
                items[idx].IsSimilarSuspect = true;

        QualityNote = groups.Count == 0
            ? ""
            : "⚠ 서술문이 서로 매우 유사: " +
              string.Join(", ", groups.Select(g => string.Join("·", g.Select(i => items[i].No)) + "번")) +
              " — [다르게 다시 생성]을 쓰거나, 학생별 특기사항을 넣으면 더 잘 구별됩니다.";
    }

    /// <summary>유사(복붙 의심) 학생을 표현을 달리해 다시 생성 — 그 학생만 재생성.</summary>
    internal void RegenerateDistinct(StudentGenItem item)
    {
        item.RestoreResult("⏳ 대기 중...");
        _queue.Enqueue(new[] { item.ToJob(_scales.Active, DistinctHint) });
    }

    /// <summary>생성 결과 영속화 — 생성 완료·수동 편집 시 호출됨.</summary>
    internal void PersistResult(StudentGenItem item)
    {
        if (SelectedSubject is null) return;
        _store.Set(SelectedSubject, item.No, item.Name,
            item.HasValidResult ? item.Result : "");
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

}

/// <summary>과목 선택 대화상자의 과목 한 줄 (전과목 생성·입력 공용).</summary>
public sealed class SubjectPick : ObservableObject
{
    public SubjectPick(string name, int eligibleCount, string? summary = null)
    {
        Name = name;
        EligibleCount = eligibleCount;
        _isChecked = eligibleCount > 0;
        Summary = summary ?? (eligibleCount > 0 ? $"{eligibleCount}명 생성 가능" : "생성할 학생 없음 (등급 미입력)");
    }

    public string Name { get; }
    public int EligibleCount { get; }
    public string Summary { get; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
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
        GenerateCommand = new RelayCommand(() => _parent.EnqueueSingle(this));
        RegenerateDistinctCommand = new RelayCommand(() => _parent.RegenerateDistinct(this));
    }

    /// <summary>백그라운드 큐 작업으로 변환 — 화면 항목과 독립적으로 실행 가능.</summary>
    internal Services.GenJob ToJob(NeisAutoFill.Core.Scale.GradeScale scale, string? variationHint = null) =>
        new(_subject, No, Name, _domains, _student.SpecialNote, scale, variationHint);

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
            if (SetProperty(ref _result, value))
            {
                RaiseByteInfo();
                if (!Busy) _parent.PersistResult(this);   // 수동 편집도 즉시 저장 (생성 중 중간값은 제외)
            }
        }
    }

    /// <summary>저장된 서술문 복원 — 저장 트리거 없이 값만 세팅.</summary>
    public void RestoreResult(string text)
    {
        _result = text;
        OnPropertyChanged(nameof(Result));
        RaiseByteInfo();
    }

    private void RaiseByteInfo()
    {
        OnPropertyChanged(nameof(ByteInfo));
        OnPropertyChanged(nameof(OverLimit));
    }

    /// <summary>UTF-8 바이트 수 표시 (나이스 바이트 제한 참고). 품질 점검이 켜져 있고 유효 결과일 때만.</summary>
    public string ByteInfo => _parent.ShowQuality && HasValidResult
        ? $"{NeisAutoFill.Core.TextMetrics.Utf8Bytes(Result):N0} byte" : "";
    /// <summary>설정된 바이트 제한 초과 여부.</summary>
    public bool OverLimit => _parent.ShowQuality && _parent.MaxNarrativeBytes > 0 && HasValidResult
        && NeisAutoFill.Core.TextMetrics.Utf8Bytes(Result) > _parent.MaxNarrativeBytes;

    private bool _isSimilarSuspect;
    /// <summary>다른 학생과 서술문이 매우 유사(복붙 의심).</summary>
    public bool IsSimilarSuspect { get => _isSimilarSuspect; set => SetProperty(ref _isSimilarSuspect, value); }

    /// <summary>유사 학생을 표현 달리해 다시 생성 (⚠유사 배지 옆 버튼).</summary>
    public ICommand RegenerateDistinctCommand { get; }

    private string _uploadState = "";
    public string UploadState { get => _uploadState; set => SetProperty(ref _uploadState, value); }

    /// <summary>나이스 입력 대상이 될 수 있는 유효한 생성 결과인지.</summary>
    public bool HasValidResult =>
        !string.IsNullOrWhiteSpace(Result) &&
        !Result.StartsWith("[오류]") && !Result.StartsWith("⏳");

    private bool _busy;
    public bool Busy { get => _busy; set => SetProperty(ref _busy, value); }

    public ICommand GenerateCommand { get; }
}
