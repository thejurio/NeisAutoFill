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

    // ── 전담 서술문 (F9 M11) — 창 안에서 교과·학년반을 골라 세특 생성·입력. null 이면 담임(기존) ──
    /// <summary>전담 서술문 창 컨텍스트. 데이터 조립·narratives 전환은 메인이 맡는다.</summary>
    /// <param name="Subjects">전담 전 과목</param>
    /// <param name="ClassesOf">과목 → 그 과목을 가르치는 (학년,반) 목록</param>
    /// <param name="Load">(학년,반,과목) → 성적표+계획 (narratives 도 그 반으로 전환). 과목 없으면 null</param>
    /// <param name="InitialSubject">창 열 때 기본 선택 과목 (메인에서 보던 것)</param>
    /// <param name="InitialClass">창 열 때 기본 선택 (학년,반)</param>
    public sealed record SubjectModeGen(
        IReadOnlyList<string> Subjects,
        Func<string, IReadOnlyList<(int Grade, string Class)>> ClassesOf,
        Func<int, string, string, (SubjectSheet Sheet, IReadOnlyList<SubjectPlan> Plans)?> Load,
        string? InitialSubject,
        (int Grade, string Class)? InitialClass);

    private readonly SubjectModeGen? _sm;
    private SubjectSheet? _smSheet;                       // 현재 (과목·반) 로드된 성적표
    private IReadOnlyList<SubjectPlan> _smPlans = System.Array.Empty<SubjectPlan>();
    public bool IsSubjectMode => _sm is not null;

    // 입력·생성 버튼 라벨 — 전담은 (교과·반) 하나라 "이 반" 표현, 담임은 "과목" 표현 (F9 M11)
    /// <summary>생성 버튼: 담임 "🚀 이 과목 생성" / 전담 "🚀 이 반 생성".</summary>
    public string GenerateOneLabel => IsSubjectMode ? "🚀 이 반 생성" : "🚀 이 과목 생성";
    /// <summary>입력 버튼: 담임 "▶ 이 과목 입력" / 전담 "▶ 이 반 입력"(지금 반+과목 하나).</summary>
    public string UploadOneLabel => IsSubjectMode ? "▶ 이 반 입력" : "▶ 이 과목 입력";
    /// <summary>배치 입력: 담임 "🚀 전과목 입력" / 전담 "🚀 이 과목 전체반 입력"(이 과목이 있는 모든 반).</summary>
    public string UploadAllLabel => IsSubjectMode ? "🚀 이 과목 전체반 입력" : "🚀 전과목 입력";
    /// <summary>전과목 '생성' 배치는 전담에선 과목 하나라 숨긴다 (입력은 전체 반이므로 유지).</summary>
    public bool ShowBatchGenerate => !IsSubjectMode;

    /// <summary>내부에서 쓰는 시트/계획 — 전담이면 선택된 (과목·반) 것, 담임이면 주입된 것.</summary>
    private IReadOnlyList<SubjectSheet> Sheets() =>
        _sm is not null ? (_smSheet is { } s ? new[] { s } : System.Array.Empty<SubjectSheet>()) : _getSheets();
    private IReadOnlyList<SubjectPlan> Plans() => _sm is not null ? _smPlans : _getPlans();
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
        Action<string> mainLog,
        SubjectModeGen? subjectMode = null)
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
        _sm = subjectMode;

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

        if (_sm is not null) InitSubjectModeSelectors();   // 전담: 교과·학년반 선택기 채우고 초기 조합 로드
        RefreshSubjects();
    }

    // ── 전담 서술문 창의 교과·학년반 선택기 (F9 M11) ──
    /// <summary>전담 전 과목 (교과 콤보).</summary>
    public ObservableCollection<string> GenSubjects { get; } = new();
    /// <summary>선택 과목을 가르치는 학년·반 (반 콤보). 예: "3-2".</summary>
    public ObservableCollection<string> GenClasses { get; } = new();
    private readonly Dictionary<string, (int Grade, string Class)> _genClassMap = new();

    private string? _genSubject;
    /// <summary>교과 선택 — 바꾸면 그 과목의 반 목록을 갱신하고 첫 반을 로드.</summary>
    public string? GenSubject
    {
        get => _genSubject;
        set { if (SetProperty(ref _genSubject, value)) { RefreshGenClasses(); LoadCurrentUnit(); } }
    }

    private string? _genClass;
    /// <summary>학년·반 선택 — 바꾸면 그 반 자료를 로드.</summary>
    public string? GenClass
    {
        get => _genClass;
        set { if (SetProperty(ref _genClass, value)) LoadCurrentUnit(); }
    }

    private void InitSubjectModeSelectors()
    {
        foreach (var s in _sm!.Subjects) GenSubjects.Add(s);
        _genSubject = _sm.InitialSubject is { } s0 && GenSubjects.Contains(s0) ? s0 : GenSubjects.FirstOrDefault();
        RefreshGenClasses();
        var initKey = _sm.InitialClass is { } ic ? $"{ic.Grade}-{ic.Class}" : null;
        _genClass = initKey is not null && GenClasses.Contains(initKey) ? initKey : GenClasses.FirstOrDefault();
        LoadUnitData();   // _smSheet/_smPlans 만 채움 (RefreshSubjects 는 생성자에서 이어 호출)
    }

    /// <summary>창을 열 때마다 메인에서 지금 보는 (과목·반)으로 선택기를 맞춘다 (F9 M11).
    /// 생성기는 싱글턴이라 첫 생성값에 고정되므로, 열 때마다 이 메서드로 동기화한다.</summary>
    public void FocusUnit(string? subject, (int Grade, string Class)? cls)
    {
        if (_sm is null) return;
        if (subject is not null && GenSubjects.Contains(subject)) _genSubject = subject;
        OnPropertyChanged(nameof(GenSubject));
        RefreshGenClasses();   // 과목의 반 목록 재구성 (없으면 첫 반)

        var key = cls is { } c ? $"{c.Grade}-{c.Class}" : null;
        if (key is not null && GenClasses.Contains(key)) _genClass = key;
        OnPropertyChanged(nameof(GenClass));

        LoadCurrentUnit();     // 그 조합 로드 + 화면 갱신
    }

    private void RefreshGenClasses()
    {
        GenClasses.Clear();
        _genClassMap.Clear();
        if (_genSubject is null) return;
        foreach (var (g, c) in _sm!.ClassesOf(_genSubject))
        {
            var key = $"{g}-{c}";
            _genClassMap[key] = (g, c);
            GenClasses.Add(key);
        }
        if (_genClass is null || !GenClasses.Contains(_genClass))
            _genClass = GenClasses.FirstOrDefault();
        OnPropertyChanged(nameof(GenClass));
    }

    /// <summary>선택된 (교과·반)의 성적표·계획을 로드하고 화면을 갱신.</summary>
    private void LoadCurrentUnit()
    {
        LoadUnitData();
        RefreshSubjects();
    }

    private void LoadUnitData()
    {
        _smSheet = null;
        _smPlans = Array.Empty<SubjectPlan>();
        if (_genSubject is null || _genClass is null || !_genClassMap.TryGetValue(_genClass, out var gc)) return;
        var loaded = _sm!.Load(gc.Grade, gc.Class, _genSubject);
        if (loaded is { } lv) { _smSheet = lv.Sheet; _smPlans = lv.Plans; }
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
        var sheets = Sheets();
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
        set
        {
            if (SetProperty(ref _selectedSubject, value))
            {
                RebuildStudents();
                OnPropertyChanged(nameof(SubjectTargetChars));
            }
        }
    }

    /// <summary>이 과목만의 목표 글자 수 (빈 값 = 전역 설정 사용). 설정에 즉시 저장.</summary>
    public string SubjectTargetChars
    {
        get => SelectedSubject is { } s
            && _settings.Options.SubjectTargetChars.TryGetValue(s, out var n) && n > 0
            ? n.ToString() : "";
        set
        {
            if (SelectedSubject is not { } s) return;
            var dict = new Dictionary<string, int>(_settings.Options.SubjectTargetChars);
            if (int.TryParse(value, out var n) && n > 0) dict[s] = n;
            else dict.Remove(s);   // 빈 값·0 = 과목별 지정 해제(전역 사용)
            _settings.Options = _settings.Options with { SubjectTargetChars = dict };
            _settings.Save();
            OnPropertyChanged();
        }
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
        foreach (var sheet in Sheets())
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

        // 전담: 지금 교과를 여러 반에 순회 세특 입력 (성적 [전체 반 입력]과 대칭)
        if (_sm is not null) { await UploadAllClassesAsync(); return; }

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
        // 화면 과목 목록을 읽어 매핑 자동 제안 (이름이 다르면 사용자가 창에서 고른다)
        await PopulateScreenMappingAsync(picks);

        var win = new BatchGenerateWindow(picks,
            title: "전과목 서술문 입력",
            description: "나이스에 입력할 과목을 선택하세요. 나이스 화면이 [학기말 종합의견] 조회 화면인지 확인한 뒤 시작하세요.",
            startLabel: "🚀 입력 시작",
            warning: "각 과목 입력 후 검증을 통과하면 나이스 [저장]을 자동으로 누르고 다음 과목으로 넘어갑니다. " +
                     "검증에 실패한 과목은 저장하지 않고 그 자리에서 중단합니다.")
        { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        var chosen = picks.Where(p => p.IsChecked).ToList();
        var chosenNames = chosen.Select(p => p.Name).ToHashSet();
        var bySubject = allBySubject.Where(kv => chosenNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (bySubject.Count == 0) return;
        var targets = BuildTargets(chosen);

        var progress = new Progress<Automation.Abstractions.ProgressInfo>(p =>
        {
            if (!string.IsNullOrEmpty(p.Message)) _mainLog(p.Message);
        });

        // runSubject 를 델리게이트로 빼 재시도 때 그대로 재사용
        Func<string, Task<Automation.BatchUploadRunner.SubjectResult>> runSubject = async subject =>
        {
            var subjectEntries = bySubject[subject];
            var report = await _engine.RunNarrativesAsync(
                subject, subjectEntries, dryRun: false, _settings.Options.MaxNarrativeBytes,
                progress, CancellationToken.None, BuildNarrativeResolveMatch(subjectEntries));
            return new Automation.BatchUploadRunner.SubjectResult(
                report.Done.Count, report.Failed, report.Skipped.Count,
                report.Skipped.Any(s => s.Reason == "사용자 취소"));
        };

        var outcomes = await Automation.BatchUploadRunner.RunAsync(
            targets, _engine, runSubject, _mainLog, unit: "명", CancellationToken.None);

        _mainLog("전과목 서술문 입력 결과: " +
            string.Join(" / ", Automation.BatchUploadRunner.Summarize(outcomes)));

        var screenByDisplay = targets.ToDictionary(t => t.Display, t => t.Screen);
        BatchResultWindow.ShowResult(outcomes, "명",
            retry: async subs =>
            {
                var rt = subs.Select(d => new Automation.BatchUploadRunner.SubjectTarget(
                    d, screenByDisplay.TryGetValue(d, out var sc) ? sc : d)).ToList();
                return await Automation.BatchUploadRunner.RunAsync(rt, _engine, runSubject, _mainLog, "명", CancellationToken.None);
            },
            owner: Application.Current.MainWindow);
    }

    /// <summary>전담: 지금 교과의 세특을 고른 여러 반에 순회 입력 (F9 M11 — 성적 전체 반 입력과 대칭).
    /// 각 반마다 세특 화면 이동→학년·반·교과 선택→조회→입력, 통과 시 나이스 [저장] 후 다음 반.</summary>
    private async Task UploadAllClassesAsync()
    {
        var subject = _genSubject;
        if (subject is null) return;

        // 각 반의 세특 서술문 수집 (반별 store 를 로드해 읽음)
        var perClass = new List<(int Grade, string Class, List<NarrativeEntry> Entries)>();
        foreach (var (g, c) in _sm!.ClassesOf(subject))
        {
            if (_sm.Load(g, c, subject) is not { } lv) continue;   // narratives 도 이 반으로 전환됨
            var rows = new List<NarrativeEntry>();
            foreach (var st in lv.Sheet.Students)
            {
                var text = _store.Get(subject, st.No, st.Name);
                if (!string.IsNullOrWhiteSpace(text)) rows.Add(new NarrativeEntry(st.No, st.Name, text!.Trim()));
            }
            if (rows.Count > 0) perClass.Add((g, c, rows));
        }
        LoadUnitData();   // 보던 반으로 복원

        if (perClass.Count == 0)
        {
            MessageBox.Show("입력할 세특 서술문이 없습니다. 먼저 생성해 주세요.", "안내",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picks = perClass.Select(pc =>
        {
            var pick = new SubjectPick($"{pc.Grade}-{pc.Class}", pc.Entries.Count, $"세특 {pc.Entries.Count}명 입력 예정");
            pick.IsChecked = true;
            return pick;
        }).ToList();

        var win = new BatchGenerateWindow(picks,
            title: $"'{subject}' 여러 반 세특 입력",
            description: $"'{subject}' 세특을 입력할 반을 선택하세요. 각 반으로 이동·조회한 뒤 자동 입력합니다.",
            startLabel: "🚀 입력 시작",
            warning: "각 반 입력 후 검증을 통과하면 나이스 [저장]을 자동으로 누르고 다음 반으로 넘어갑니다. " +
                     "검증에 실패한 반은 저장하지 않고 그 자리에서 중단합니다.")
        { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        var chosen = picks.Where(p => p.IsChecked).Select(p => p.Name).ToHashSet();
        var targets = perClass.Where(pc => chosen.Contains($"{pc.Grade}-{pc.Class}"))
            .Select(pc => new Automation.BatchUploadRunner.SubjectTarget($"{pc.Grade}-{pc.Class}", $"{pc.Grade}-{pc.Class}"))
            .ToList();
        if (targets.Count == 0) return;

        var progress = new Progress<Automation.Abstractions.ProgressInfo>(p =>
        {
            if (!string.IsNullOrEmpty(p.Message)) _mainLog(p.Message);
        });

        Func<string, Task<Automation.BatchUploadRunner.SubjectResult>> runClass = async classKey =>
        {
            var pc = perClass.First(x => $"{x.Grade}-{x.Class}" == classKey);
            _mainLog($"{pc.Grade}-{pc.Class} {subject} 세특 화면을 준비하고 있어요…");
            if (!await _engine.NavigateToAsync(Automation.Abstractions.NeisTarget.SubjectDevelopment, progress))
                return Fail($"{classKey} 세특 화면 이동 실패");
            var (okAxis, whyAxis) = await _engine.SelectNarrativeAxisAsync(pc.Grade, pc.Class, subject, progress);
            if (!okAxis) return Fail($"{classKey} 학년·반·교과 선택 실패: {whyAxis}");
            var (okQ, whyQ) = await _engine.QueryAsync();
            if (!okQ) return Fail($"{classKey} 조회 실패: {whyQ}");

            var report = await _engine.RunNarrativesAsync(
                subject, pc.Entries, dryRun: false, _settings.Options.MaxNarrativeBytes,
                progress, CancellationToken.None, BuildNarrativeResolveMatch(pc.Entries));
            if (report.Failed.Count == 0)
            {
                var (okSave, whySave) = await _engine.SaveScreenAsync();
                if (!okSave) _mainLog($"  ⚠ {classKey} 나이스 저장 건너뜀: {whySave}");
            }
            return new Automation.BatchUploadRunner.SubjectResult(
                report.Done.Count, report.Failed, report.Skipped.Count,
                report.Skipped.Any(s => s.Reason == "사용자 취소"));
        };

        var outcomes = await Automation.BatchUploadRunner.RunAsync(
            targets, _engine, runClass, _mainLog, unit: "반", CancellationToken.None);
        _mainLog($"'{subject}' 여러 반 세특 입력 결과: " +
            string.Join(" / ", Automation.BatchUploadRunner.Summarize(outcomes)));

        BatchResultWindow.ShowResult(outcomes, "반",
            retry: async subs =>
            {
                var rt = subs.Select(k => new Automation.BatchUploadRunner.SubjectTarget(k, k)).ToList();
                return await Automation.BatchUploadRunner.RunAsync(rt, _engine, runClass, _mainLog, "반", CancellationToken.None);
            },
            owner: Application.Current.MainWindow);
    }

    private static Automation.BatchUploadRunner.SubjectResult Fail(string reason) =>
        new(0, new[] { new SkipItem("", "", "", reason) }, 0, false);

    /// <summary>선택된 과목들로 (내 과목 → 화면 과목) 대상 목록. 매핑 없거나 '미선택'이면 같은 이름.</summary>
    private static List<Automation.BatchUploadRunner.SubjectTarget> BuildTargets(IEnumerable<SubjectPick> chosen) =>
        chosen.Select(p =>
        {
            var screen = p.HasScreenOptions && p.ScreenSubject != SubjectPick.NoScreenMatch
                ? p.ScreenSubject : p.Name;
            return new Automation.BatchUploadRunner.SubjectTarget(p.Name, screen);
        }).ToList();

    /// <summary>화면 과목 목록을 읽어 각 pick 에 자동 매핑을 채운다. 못 읽으면 매핑 UI 없이 같은 이름.</summary>
    private async Task PopulateScreenMappingAsync(IReadOnlyList<SubjectPick> picks)
    {
        try
        {
            var screen = await _engine.ReadSubjectOptionsAsync();
            if (screen.Count == 0) return;
            var suggestions = Core.SubjectMapper.Suggest(picks.Select(p => p.Name).ToList(), screen);
            for (int i = 0; i < picks.Count; i++)
                picks[i].SetScreenMapping(screen, suggestions[i].Screen, suggestions[i].Auto);
        }
        catch (Exception ex) { Diag.Swallow(ex, "전과목(서술문) 화면 과목 매핑"); }   // 같은 이름으로 진행
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
        foreach (var sheet in Sheets())
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
        _plans = Plans();
        PlanStatus = _plans.Count > 0
            ? $"평가계획: {string.Join(", ", _plans.Select(p => $"{p.SubjectName} {p.Domains.Count}영역"))}"
            : "(평가계획 없음 — 메인 [📁 자료 준비]에서 입력하거나 불러오면 평가기준이 서술문에 반영됩니다)";

        SubjectNames.Clear();
        foreach (var s in Sheets()) SubjectNames.Add(s.SubjectName);
        if (SelectedSubject is null || !SubjectNames.Contains(SelectedSubject))
            SelectedSubject = SubjectNames.FirstOrDefault();
        else
            RebuildStudents();   // 같은 과목 유지 시에도 데이터 변경 반영
    }

    private void RebuildStudents()
    {
        Students.Clear();
        var sheet = Sheets().FirstOrDefault(s => s.SubjectName == SelectedSubject);
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

    /// <summary>
    /// 서술문 입력 전 학생 매칭 확인 콜백 (등급과 동일한 확인 창 재사용, 학생 매핑만 노출).
    /// 이름이 달라 자동 매칭 안 되는 학생을 사용자가 연결한다. Clean 이면 창 없이 진행.
    /// </summary>
    private Func<Automation.Abstractions.MatchContext, Task<Automation.Abstractions.MatchDecision?>>
        BuildNarrativeResolveMatch(IReadOnlyList<NarrativeEntry> entries) => ctx =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var issues = MatchAnalyzer.AnalyzeNarratives(
                ctx.ScreenSubject, ctx.TargetSubject, ctx.RowMap, entries.Select(e => e.Name).ToList());
            if (issues.Clean)
                return new Automation.Abstractions.MatchDecision(StudentMatcher.MatchMode.ByName);

            // 과목만 다르면 간단 확인 (등급과 동일 UX)
            if (issues.SubjectOnlyMismatch)
            {
                var ok = MessageBox.Show(
                    $"화면 과목은 '{issues.ScreenSubject}'인데 입력 대상은 '{ctx.TargetSubject}'입니다.\n" +
                    "학생은 화면과 일치합니다. 이 화면에 그대로 입력할까요?",
                    "과목 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                return ok ? new Automation.Abstractions.MatchDecision(StudentMatcher.MatchMode.ByName)
                          : (Automation.Abstractions.MatchDecision?)null;
            }

            // 학생 이름 불일치 → 확인 창 재사용 (합성 sheet 로 학생 옵션 제공; 영역은 없음)
            var sheet = new SubjectSheet(ctx.TargetSubject, System.Array.Empty<string>(),
                entries.Select(e => new Student(e.No, e.Name, new Dictionary<string, string>())).ToList());
            var vm = new MatchPreviewViewModel(issues, sheet);
            var win = new MatchPreviewWindow(vm) { Owner = Application.Current.MainWindow };
            return win.ShowDialog() == true ? vm.BuildDecision() : null;
        }).Task;

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

        // 전담이면 현재 선택된 (학년,반,과목) — 세특 이동·조회에 사용
        (int Grade, string Class, string Subject)? ctx =
            _sm is not null && _genSubject is not null && _genClass is not null
            && _genClassMap.TryGetValue(_genClass, out var gc)
                ? (gc.Grade, gc.Class, _genSubject)
                : null;
        if (!dryRun)
        {
            var prompt = ctx is { } c
                ? $"'{c.Grade}-{c.Class} {c.Subject}' 세특 {items.Count}명을 나이스에 입력합니다.\n" +
                  "교과학습발달상황 화면으로 이동·조회한 뒤 자동 입력합니다. (저장은 안 하며, 확인 후 나이스에서 직접 [저장])\n\n계속할까요?"
                : $"'{SelectedSubject}' {items.Count}명의 서술문을 나이스 화면에 입력합니다.\n" +
                  "(저장은 하지 않으며, 확인 후 나이스에서 직접 [저장]을 누르세요)\n\n" +
                  "나이스 화면이 해당 과목의 종합의견 입력 화면인지 확인하셨나요?";
            var ok = MessageBox.Show(prompt, "확인", MessageBoxButton.YesNo, MessageBoxImage.Question);
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
            // 전담: 교과학습발달상황(세특) 화면으로 이동 → 학년·반·교과 선택 → 조회 (담임은 현재 화면 그대로)
            if (ctx is { } tc && !dryRun)
            {
                _mainLog($"{tc.Grade}-{tc.Class} {tc.Subject} 세특 화면을 준비하고 있어요…");
                var moved = await _engine.NavigateToAsync(
                    Automation.Abstractions.NeisTarget.SubjectDevelopment, progress);
                if (!moved) { MessageBox.Show("교과학습발달상황 화면으로 이동하지 못했어요.", "안내", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                var (okAxis, whyAxis) = await _engine.SelectNarrativeAxisAsync(tc.Grade, tc.Class, tc.Subject, progress);
                if (!okAxis) { MessageBox.Show($"학년·반·교과를 맞추지 못했어요.\n{whyAxis}", "안내", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
                var (okQ, whyQ) = await _engine.QueryAsync();
                if (!okQ) { MessageBox.Show($"명단을 불러오지 못했어요.\n{whyQ}", "안내", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            }

            var report = await _engine.RunNarrativesAsync(
                SelectedSubject, entries, dryRun,
                _settings.Options.MaxNarrativeBytes, progress, CancellationToken.None,
                BuildNarrativeResolveMatch(entries));

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
    public const string NoScreenMatch = "(화면에서 선택)";

    public SubjectPick(string name, int eligibleCount, string? summary = null)
    {
        Name = name;
        EligibleCount = eligibleCount;
        _isChecked = eligibleCount > 0;
        Summary = summary ?? (eligibleCount > 0 ? $"{eligibleCount}명 생성 가능" : "생성할 학생 없음 (등급 미입력)");
        _screenSubject = name;   // 기본은 같은 이름
    }

    public string Name { get; }
    public int EligibleCount { get; }
    public string Summary { get; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }

    // ── 화면 과목 매핑 (전과목 입력에서만 사용; 생성에선 옵션 비어 숨김) ──
    /// <summary>이 과목을 입력할 화면 콤보의 과목명. 기본은 같은 이름, 다르면 사용자가 고른다.</summary>
    private string _screenSubject;
    public string ScreenSubject { get => _screenSubject; set => SetProperty(ref _screenSubject, value); }

    /// <summary>화면에서 고를 수 있는 과목 목록 (+ 미선택). 비어 있으면 매핑 UI 안 보임.</summary>
    public IReadOnlyList<string> ScreenOptions { get; private set; } = System.Array.Empty<string>();
    public bool HasScreenOptions => ScreenOptions.Count > 0;

    /// <summary>자동 매칭이 안 돼 사용자 확인이 필요한지 (강조 표시용).</summary>
    private bool _needsAttention;
    public bool NeedsAttention { get => _needsAttention; set => SetProperty(ref _needsAttention, value); }

    /// <summary>화면 과목 목록·자동 제안을 채운다 (전과목 입력 시작 전).</summary>
    public void SetScreenMapping(IReadOnlyList<string> options, string? suggested, bool auto)
    {
        var opts = new List<string>(options) { NoScreenMatch };
        ScreenOptions = opts;
        ScreenSubject = suggested ?? NoScreenMatch;
        NeedsAttention = !auto;
        OnPropertyChanged(nameof(HasScreenOptions));
        OnPropertyChanged(nameof(ScreenOptions));
    }
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
