using System.Collections.ObjectModel;
using System.Data;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.App.ViewModels;

/// <summary>
/// 명단·평가계획 인앱 편집. 저장 시 SubjectPlan 목록과 명단을 재조립해
/// PlanWorkbookWriter 로 엑셀에 쓰고 메인이 다시 로드한다 (엑셀 직접 수정과 같은 경로).
/// </summary>
public sealed class PlanEditorViewModel : ObservableObject
{
    private const int EmptyRosterRows = 30;   // 빈 명단일 때 미리 깔아두는 빈 셀 행 수

    private readonly GradeScale _scale;

    // 담임 import: 과목 목록 인식 후 selectSubjects 로 고른 과목만 불러온다 (F9 M4b)
    private readonly Func<string, IProgress<string>,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>?,
        Task<IReadOnlyList<SubjectPlan>>>? _importer;

    // 전담 import: (학년·과목) 단위 인식 후 selectUnits 로 고른 것만 학년별로 불러온다 (F9 M4b)
    private readonly Func<string, IProgress<string>,
        Func<IReadOnlyList<PlanUnit>, Task<IReadOnlyList<PlanUnit>?>>?,
        Task<IReadOnlyList<NeisAutoFill.Generator.GasPlanImporter.GradePlanSet>>>? _unitImporter;

    // ── 전담 모드 (F9 M4a) — null 이면 담임(기존 동작) ──
    private readonly Services.SubjectModeStore? _subjectStore;
    private NeisAutoFill.Core.ClassRef? _currentClass;   // 현재 편집 중인 반 (명단)
    private int _currentGrade;                            // 현재 편집 중인 학년 (계획)

    /// <summary>전담 모드인가 (학년·반 콤보 표시).</summary>
    public bool IsSubjectMode => _subjectStore is not null;

    public PlanEditorViewModel(
        IReadOnlyList<SubjectPlan> plans,
        IReadOnlyList<(string No, string Name)> roster,
        GradeScale scale,
        Func<string, IProgress<string>,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>?,
            Task<IReadOnlyList<SubjectPlan>>>? importer = null,
        Services.SubjectModeStore? subjectStore = null,
        NeisAutoFill.Core.ClassRef? initial = null,
        Func<string, IProgress<string>,
            Func<IReadOnlyList<PlanUnit>, Task<IReadOnlyList<PlanUnit>?>>?,
            Task<IReadOnlyList<NeisAutoFill.Generator.GasPlanImporter.GradePlanSet>>>? unitImporter = null)
    {
        _scale = scale;
        _importer = importer;
        _unitImporter = unitImporter;
        _subjectStore = subjectStore;

        // 커맨드를 먼저 만든다 — SelectedSubject setter 가 커맨드를 참조하므로 데이터 채우기보다 앞서야 함
        ImportPlanCommand = new AsyncRelayCommand(ImportPlanAsync, () => !IsImporting);
        AddSubjectCommand = new RelayCommand(AddSubject);
        RemoveSubjectCommand = new RelayCommand(RemoveSubject, () => SelectedSubject is not null);
        AddRosterRowCommand = new RelayCommand(() => Roster.Add(new RosterRow()));
        RemoveRosterRowCommand = new RelayCommand(RemoveRosterRow, () => SelectedRosterRow is not null);
        ClearRosterCommand = new RelayCommand(ClearRoster);
        ClearAllPlansCommand = new RelayCommand(ClearAllPlans);

        // 이름 입력 시 자동 번호 부여를 위해 행 추가/제거 때 감시를 붙인다
        Roster.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (RosterRow row in e.NewItems) row.PropertyChanged += RosterRow_PropertyChanged;
            if (e.OldItems is not null)
                foreach (RosterRow row in e.OldItems) row.PropertyChanged -= RosterRow_PropertyChanged;
        };

        if (_subjectStore is null)
        {
            // 담임: 넘겨받은 명단·계획 그대로
            foreach (var (no, name) in roster) Roster.Add(new RosterRow { No = no, Name = name });
            while (Roster.Count < EmptyRosterRows) Roster.Add(new RosterRow());
            foreach (var p in plans) Subjects.Add(new PlanSubjectEdit(p, scale));
            SelectedSubject = Subjects.FirstOrDefault();
        }
        else
        {
            // 전담: 등록된 반·학년으로 콤보 채우고 첫 항목 로드
            AddClassCommand = new RelayCommand(AddClass);
            RemoveClassCommand = new RelayCommand(RemoveClass, () => SelectedClassRef is not null);
            foreach (var c in _subjectStore.ListClasses().OrderBy(c => c.Grade).ThenBy(c => ClassNum(c.Class)))
                ClassOptions.Add(c);
            // ★ 학년 = 계획 파일 ∪ 반의 학년 — 빈 계획은 파일이 없어도 반이 있으면 학년이 유지된다
            var grades = _subjectStore.ListGrades()
                .Concat(ClassOptions.Select(c => c.Grade)).Distinct().OrderBy(g => g);
            foreach (var g in grades) GradeOptions.Add(g);
            // 메인에서 보던 반(initial)이 있으면 그걸 기본값으로 — 없으면 첫 항목 (ClassRef 는 struct)
            _selectedClassRef = initial is { } iv && ClassOptions.Any(c => c.Key == iv.Key)
                ? iv
                : (ClassOptions.Count > 0 ? ClassOptions[0] : (NeisAutoFill.Core.ClassRef?)null);
            _selectedGrade = initial is { } gv && GradeOptions.Contains(gv.Grade)
                ? gv.Grade
                : (_selectedClassRef?.Grade ?? GradeOptions.FirstOrDefault());
            LoadCurrentClass();   // 명단
            LoadCurrentGrade();   // 계획
        }
    }

    public ObservableCollection<RosterRow> Roster { get; } = new();
    public ObservableCollection<PlanSubjectEdit> Subjects { get; } = new();

    // ── 전담: 학년·반 축 ──────────────────
    public ObservableCollection<NeisAutoFill.Core.ClassRef> ClassOptions { get; } = new();
    public ObservableCollection<int> GradeOptions { get; } = new();

    private NeisAutoFill.Core.ClassRef? _selectedClassRef;
    /// <summary>현재 편집 중인 반. 바꾸면 그 반 명단을 저장하고 새 반을 로드.</summary>
    public NeisAutoFill.Core.ClassRef? SelectedClassRef
    {
        get => _selectedClassRef;
        set
        {
            if (_selectedClassRef.Equals(value)) return;
            SaveCurrentClass();                     // 떠나기 전 저장
            _selectedClassRef = value;
            OnPropertyChanged();
            LoadCurrentClass();
        }
    }

    private int _selectedGrade;
    /// <summary>현재 편집 중인 학년(계획). 바꾸면 그 학년 계획을 저장하고 새 학년을 로드.</summary>
    public int SelectedGrade
    {
        get => _selectedGrade;
        set
        {
            if (_selectedGrade == value) return;
            SaveCurrentGrade();
            _selectedGrade = value;
            OnPropertyChanged();
            LoadCurrentGrade();
        }
    }

    public ICommand AddClassCommand { get; private set; } = null!;
    public ICommand RemoveClassCommand { get; private set; } = null!;

    private void AddClass()
    {
        var vm = new AddClassDialogViewModel();
        var win = new AddClassDialog(vm) { Owner = System.Windows.Application.Current.Windows.OfType<PlanEditorWindow>().FirstOrDefault() };
        if (win.ShowDialog() != true) return;
        var c = new NeisAutoFill.Core.ClassRef(vm.Grade, vm.ClassName.Trim());

        if (ClassOptions.Contains(c))
        {
            System.Windows.MessageBox.Show($"{c.Grade}-{c.Class} 반은 이미 있습니다.",
                "안내", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            SelectedClassRef = c;
            return;
        }

        InsertSorted(c);   // 오름차순 자리에 삽입
        SelectedClassRef = c;
        // 반을 추가하면 그 학년의 평가계획도 자동으로 준비된다 (학년은 명단에서만 추가)
        if (!GradeOptions.Contains(c.Grade))
        {
            GradeOptions.Add(c.Grade);
            SortInts(GradeOptions);
        }
        SelectedGrade = c.Grade;   // 계획도 이 학년으로 전환
    }

    /// <summary>반을 (학년, 반번호) 오름차순 자리에 끼워넣는다.</summary>
    private void InsertSorted(NeisAutoFill.Core.ClassRef c)
    {
        int i = 0;
        while (i < ClassOptions.Count &&
               (ClassOptions[i].Grade < c.Grade ||
                (ClassOptions[i].Grade == c.Grade && ClassNum(ClassOptions[i].Class) < ClassNum(c.Class))))
            i++;
        ClassOptions.Insert(i, c);
    }

    /// <summary>반 이름을 정렬용 숫자로 (숫자 아니면 큰 값 뒤로).</summary>
    private static int ClassNum(string cls) => int.TryParse(cls, out var n) ? n : int.MaxValue;

    private static void SortInts(ObservableCollection<int> col)
    {
        var sorted = col.OrderBy(x => x).ToList();
        for (int i = 0; i < sorted.Count; i++)
            if (!col[i].Equals(sorted[i])) col.Move(col.IndexOf(sorted[i]), i);
    }

    private void RemoveClass()
    {
        if (_selectedClassRef is not { } c) return;
        var r = System.Windows.MessageBox.Show(
            $"{c.Grade}-{c.Class} 반을 목록에서 지웁니다.\n(저장된 명단 파일은 남습니다 — 다시 추가하면 복원됩니다)",
            "반 삭제", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (r != System.Windows.MessageBoxResult.OK) return;

        var idx = ClassOptions.IndexOf(c);
        ClassOptions.Remove(c);
        _selectedClassRef = ClassOptions.Count > 0 ? ClassOptions[System.Math.Min(idx, ClassOptions.Count - 1)] : null;
        OnPropertyChanged(nameof(SelectedClassRef));
        LoadCurrentClass();
        // 그 학년의 반이 하나도 안 남으면 학년 선택지에서도 제거
        if (!ClassOptions.Any(x => x.Grade == c.Grade))
        {
            GradeOptions.Remove(c.Grade);
            if (_selectedGrade == c.Grade)
            {
                _selectedGrade = GradeOptions.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedGrade));
                LoadCurrentGrade();
            }
        }
    }

    private void LoadCurrentClass()
    {
        Roster.Clear();
        if (_subjectStore is not null && _selectedClassRef is { } c)
            foreach (var (no, name) in _subjectStore.LoadRoster(c))
                Roster.Add(new RosterRow { No = no, Name = name });
        while (Roster.Count < EmptyRosterRows) Roster.Add(new RosterRow());
    }

    private void SaveCurrentClass()
    {
        if (_subjectStore is null || _selectedClassRef is not { } c) return;
        var roster = Roster.Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select((r, i) => (string.IsNullOrWhiteSpace(r.No) ? (i + 1).ToString() : r.No.Trim(), r.Name.Trim()))
            .ToList();
        _subjectStore.SaveRoster(c, roster);
    }

    private void LoadCurrentGrade()
    {
        Subjects.Clear();
        if (_subjectStore is not null && _selectedGrade > 0)
            foreach (var p in _subjectStore.LoadPlan(_selectedGrade))
                Subjects.Add(new PlanSubjectEdit(p, _scale));
        SelectedSubject = Subjects.FirstOrDefault();
    }

    private void SaveCurrentGrade()
    {
        if (_subjectStore is null || _selectedGrade <= 0) return;
        var plans = new List<SubjectPlan>();
        foreach (var s in Subjects)
            if (s.BuildPlan(out _) is { Domains.Count: > 0 } plan) plans.Add(plan);
        _subjectStore.SavePlan(_selectedGrade, plans);
    }

    /// <summary>전담: 현재 반·학년을 모두 저장 (창 닫기 전 호출).</summary>
    public void SaveSubjectMode() { SaveCurrentClass(); SaveCurrentGrade(); }

    /// <summary>인식 검수 경고 (오인식·누락 의심 지점). 가져오기 후 채워진다.</summary>
    public ObservableCollection<PlanWarningVm> RecognitionWarnings { get; } = new();
    public bool HasWarnings => RecognitionWarnings.Count > 0;

    private void SetWarnings(IReadOnlyList<SubjectPlan> plans)
    {
        RecognitionWarnings.Clear();
        foreach (var w in PlanAudit.Analyze(plans, _scale.Levels.Select(l => l.Label).ToList()))
        {
            var where = w.Domain is null ? w.Subject : $"{w.Subject} · {w.Domain}";
            var icon = w.Level == PlanWarningLevel.Warn ? "⚠" : "ℹ";
            RecognitionWarnings.Add(new PlanWarningVm(
                $"{icon} {where}: {w.Message}", w.Level == PlanWarningLevel.Warn));
        }
        OnPropertyChanged(nameof(HasWarnings));
    }

    public ICommand ClearRosterCommand { get; }
    public ICommand ClearAllPlansCommand { get; }

    /// <summary>학생 명단 전부 비우기 (빈 입력 행만 남김).</summary>
    private void ClearRoster()
    {
        Roster.Clear();
        while (Roster.Count < EmptyRosterRows) Roster.Add(new RosterRow());
    }

    /// <summary>모든 과목·평가계획 비우기.</summary>
    private void ClearAllPlans()
    {
        Subjects.Clear();
        SelectedSubject = null;
    }

    /// <summary>
    /// 이름을 입력하면 번호가 빈 행에 다음 번호를 자동 부여.
    /// 다음 번호 = 현재 숫자 번호의 최댓값 + 1 — 사용자가 번호를 고쳐 빈 번호가 생겨도
    /// (예: 14 다음 16) 이어지는 자동 번호는 17이 된다.
    /// </summary>
    private void RosterRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RosterRow.Name) || sender is not RosterRow row) return;
        if (string.IsNullOrWhiteSpace(row.Name) || !string.IsNullOrWhiteSpace(row.No)) return;

        var max = Roster
            .Where(r => int.TryParse(r.No, out _))
            .Select(r => int.Parse(r.No))
            .DefaultIfEmpty(0)
            .Max();
        row.No = (max + 1).ToString();
    }

    private RosterRow? _selectedRosterRow;
    public RosterRow? SelectedRosterRow
    {
        get => _selectedRosterRow;
        set { if (SetProperty(ref _selectedRosterRow, value)) (RemoveRosterRowCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    private PlanSubjectEdit? _selectedSubject;
    public PlanSubjectEdit? SelectedSubject
    {
        get => _selectedSubject;
        set { if (SetProperty(ref _selectedSubject, value)) (RemoveSubjectCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
    }

    public string ScaleSummary => string.Join(" / ", _scale.Levels.Select(l => l.Label));

    public ICommand AddSubjectCommand { get; }
    public ICommand RemoveSubjectCommand { get; }
    public ICommand AddRosterRowCommand { get; }
    public ICommand RemoveRosterRowCommand { get; }
    public ICommand ImportPlanCommand { get; }

    // ── AI 평가계획 불러오기 (이지에듀/스쿨마스터 PDF·HWP·HWPX) ──

    private bool _isImporting;
    public bool IsImporting
    {
        get => _isImporting;
        set { if (SetProperty(ref _isImporting, value)) OnPropertyChanged(nameof(ImportStatus)); }
    }

    private string _importStatus = "";
    public string ImportStatus { get => _importStatus; set => SetProperty(ref _importStatus, value); }

    private async Task ImportPlanAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "평가계획서 (Excel/PDF/HWP)|*.xlsx;*.xlsm;*.pdf;*.hwp;*.hwpx|모든 파일|*.*",
            Title = "평가계획서 파일 선택 (엑셀=바로 / PDF·HWP=AI 인식)",
        };
        if (dlg.ShowDialog() != true) return;
        await ImportPlanFileAsync(dlg.FileName);
    }

    /// <summary>
    /// 파일 경로로 가져오기 (버튼·드래그앤드롭 공용 진입점).
    /// 확장자에 따라 처리 방식이 갈린다: xlsx/xlsm = 기존 엑셀 양식 직접 읽기, pdf/hwp/hwpx = AI 인식.
    /// </summary>
    public async Task ImportPlanFileAsync(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        var isAi = ext is not (".xlsx" or ".xlsm");

        // 전담 AI 경로는 (학년·과목) 선택 창이 곧 확인 단계 → 사전 교체 경고 생략.
        // 그 외(담임·전담 xlsx)는 현재 편집 중인 계획을 덮으므로 확인.
        if (!(IsSubjectMode && isAi) && Subjects.Any(s => s.BuildPlan(out _) is { Domains.Count: > 0 }))
        {
            var ok = System.Windows.MessageBox.Show(
                "이미 입력된 평가계획이 있습니다. 불러온 내용으로 교체할까요?",
                "확인", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (ok != System.Windows.MessageBoxResult.Yes) return;
        }

        var fileName = System.IO.Path.GetFileName(path);
        IsImporting = true;
        ImportStatus = isAi
            ? $"⏳ AI 가 '{fileName}' 을(를) 분석하는 중... (수십 초 걸릴 수 있습니다)"
            : $"⏳ '{fileName}' 읽는 중...";
        try
        {
            var progress = new Progress<string>(s => ImportStatus = $"⏳ {s}");

            // 전담 + AI: 한 파일에서 (학년·과목) 분리 인식 → 선택 → 학년별 파일로 저장 (F9 M4b)
            if (IsSubjectMode && isAi)
            {
                if (_unitImporter is null) throw new InvalidOperationException("AI 가져오기를 사용할 수 없습니다.");
                var sets = await _unitImporter(path, progress, SelectUnitsAsync);
                ApplyUnitSets(sets);
                return;
            }

            IReadOnlyList<SubjectPlan> plans;
            if (!isAi)
            {
                // 기존 엑셀 양식 — AI 없이 로컬 파싱 (명단 시트가 있고 현재 명단이 비어 있으면 명단도 채움)
                plans = Excel.PlanWorkbookLoader.Load(path, _scale);
                if (plans.Count == 0)
                    throw new InvalidOperationException(
                        "엑셀에서 평가계획을 찾지 못했습니다.\n파일의 등급 표기가 현재 척도" +
                        $"({ScaleSummary})와 같은지 확인해 주세요.");
                var roster = Excel.PlanWorkbookLoader.LoadRoster(path);
                if (roster.Count > 0 && Roster.All(r => string.IsNullOrWhiteSpace(r.Name)))
                {
                    Roster.Clear();
                    foreach (var (no, name) in roster) Roster.Add(new RosterRow { No = no, Name = name });
                }
            }
            else
            {
                if (_importer is null) throw new InvalidOperationException("AI 가져오기를 사용할 수 없습니다.");
                plans = await _importer(path, progress, SelectSubjectsAsync);   // 담임: 과목 선택 창
            }

            Subjects.Clear();
            foreach (var p in plans) Subjects.Add(new PlanSubjectEdit(p, _scale));
            SelectedSubject = Subjects.FirstOrDefault();

            SetWarnings(plans);
            var warnN = RecognitionWarnings.Count(w => w.IsWarn);
            ImportStatus = warnN > 0
                ? $"✔ {plans.Count}개 과목 인식 · ⚠ 확인 필요 {warnN}건 — 아래 목록의 과목을 표에서 확인하세요."
                : $"✔ {plans.Count}개 과목 인식 완료 — 내용을 확인·수정한 뒤 [저장 후 적용]을 누르세요.";
        }
        catch (OperationCanceledException)
        {
            ImportStatus = "";   // 사용자가 선택 창에서 취소 — 조용히 종료
        }
        catch (Exception ex)
        {
            ImportStatus = "";
            System.Windows.MessageBox.Show(ex.Message, "가져오기 실패",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally { IsImporting = false; }
    }

    /// <summary>담임: 인식된 과목 중 불러올 것을 고르는 선택 창. null = 취소.</summary>
    private Task<IReadOnlyList<string>?> SelectSubjectsAsync(IReadOnlyList<string> subjects) =>
        Task.FromResult(System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = PlanPickerViewModel.ForSubjects(subjects);
            var win = new PlanPickerWindow(vm) { Owner = OwnerWindow() };
            return win.ShowDialog() == true ? vm.SelectedSubjects() : null;
        }));

    /// <summary>전담: 인식된 (학년·과목) 중 불러올 것을 고르는 선택 창 (학년 불명은 지정). null = 취소.</summary>
    private Task<IReadOnlyList<PlanUnit>?> SelectUnitsAsync(IReadOnlyList<PlanUnit> units) =>
        Task.FromResult(System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = PlanPickerViewModel.ForUnits(units, _selectedGrade);
            var win = new PlanPickerWindow(vm) { Owner = OwnerWindow() };
            return win.ShowDialog() == true ? vm.SelectedUnits() : null;
        }));

    private static System.Windows.Window? OwnerWindow() =>
        System.Windows.Application.Current.Windows.OfType<PlanEditorWindow>().FirstOrDefault()
        ?? System.Windows.Application.Current.MainWindow;

    /// <summary>전담: 인식된 학년별 계획을 각 학년 파일에 병합 저장하고, 현재 편집 화면을 갱신한다.
    /// 같은 과목이 이미 있으면 새로 인식한 것으로 교체, 없으면 추가 (다른 과목은 보존).</summary>
    private void ApplyUnitSets(IReadOnlyList<NeisAutoFill.Generator.GasPlanImporter.GradePlanSet> sets)
    {
        if (_subjectStore is null || sets.Count == 0) { ImportStatus = ""; return; }

        int savedSubjects = 0;
        foreach (var set in sets)
        {
            var merged = _subjectStore.LoadPlan(set.Grade).ToList();   // 그 학년 기존 계획
            foreach (var p in set.Plans)
            {
                merged.RemoveAll(m => m.SubjectName == p.SubjectName);   // 같은 과목은 교체
                merged.Add(p);
                savedSubjects++;
            }
            _subjectStore.SavePlan(set.Grade, merged);

            if (!GradeOptions.Contains(set.Grade))                      // 새 학년이면 콤보에 반영
            {
                var list = GradeOptions.Concat(new[] { set.Grade }).OrderBy(g => g).ToList();
                GradeOptions.Clear();
                foreach (var g in list) GradeOptions.Add(g);
            }
        }

        // 불러온 첫 학년으로 화면을 옮겨 결과를 보여준다 (SelectedGrade setter 가 LoadCurrentGrade 호출)
        var firstGrade = sets[0].Grade;
        if (_selectedGrade == firstGrade) LoadCurrentGrade();
        else SelectedGrade = firstGrade;

        var gradeList = string.Join("·", sets.Select(s => $"{s.Grade}학년"));
        ImportStatus = $"✔ {gradeList} 총 {savedSubjects}개 과목 저장 완료 — 학년 콤보로 확인하세요.";
    }

    /// <summary>행 삭제 — 지운 번호보다 큰 번호들은 하나씩 당긴다 (18 삭제 → 19가 18로).</summary>
    private void RemoveRosterRow()
    {
        var row = SelectedRosterRow;
        if (row is null) return;
        Roster.Remove(row);

        if (int.TryParse(row.No, out var removed))
            foreach (var r in Roster)
                if (int.TryParse(r.No, out var n) && n > removed)
                    r.No = (n - 1).ToString();
    }

    private void AddSubject()
    {
        var baseName = "새과목";
        var name = baseName;
        for (int i = 2; Subjects.Any(s => s.Name == name); i++) name = $"{baseName}{i}";
        var subject = new PlanSubjectEdit(name, _scale);
        Subjects.Add(subject);
        SelectedSubject = subject;
    }

    private void RemoveSubject()
    {
        if (SelectedSubject is null) return;
        var idx = Subjects.IndexOf(SelectedSubject);
        Subjects.Remove(SelectedSubject);
        SelectedSubject = Subjects.Count > 0 ? Subjects[Math.Min(idx, Subjects.Count - 1)] : null;
    }

    /// <summary>클립보드 표를 명단으로 반영 (전체 교체).</summary>
    public int PasteRoster(string clipboardText)
    {
        var parsed = ClipboardTable.ToRoster(ClipboardTable.Parse(clipboardText));
        if (parsed.Count == 0) return 0;
        Roster.Clear();
        foreach (var (no, name) in parsed) Roster.Add(new RosterRow { No = no, Name = name });
        return parsed.Count;
    }

    /// <summary>편집 내용 검증 + (계획, 명단) 조립. 문제가 있으면 error 에 사유를 담고 null.</summary>
    public (IReadOnlyList<SubjectPlan> Plans, IReadOnlyList<(string, string)> Roster)? Build(out string? error)
    {
        var roster = Roster
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select((r, i) => (No: string.IsNullOrWhiteSpace(r.No) ? (i + 1).ToString() : r.No.Trim(),
                               Name: r.Name.Trim()))
            .ToList();

        var plans = new List<SubjectPlan>();
        var names = new HashSet<string>();
        foreach (var s in Subjects)
        {
            var name = s.Name.Trim();
            if (name == "") { error = "이름이 빈 과목이 있습니다."; return null; }
            if (!names.Add(name)) { error = $"과목명 '{name}'이(가) 중복됩니다."; return null; }

            var plan = s.BuildPlan(out var subjectError);
            if (plan is null) { error = $"[{name}] {subjectError}"; return null; }
            if (plan.Domains.Count > 0) plans.Add(plan);
        }

        error = null;
        return (plans, roster);
    }
}

/// <summary>인식 검수 경고 한 줄 (표시용). IsWarn=true 는 ⚠(누락), false 는 ℹ(참고).</summary>
public sealed record PlanWarningVm(string Text, bool IsWarn);

/// <summary>명단 한 줄 (편집용).</summary>
public sealed class RosterRow : ObservableObject
{
    private string _no = "";
    public string No { get => _no; set => SetProperty(ref _no, value); }

    private string _name = "";
    public string Name { get => _name; set => SetProperty(ref _name, value); }
}

/// <summary>
/// 한 과목의 평가계획 편집 표. 행 = 영역, 열 = 영역 | 성취기준 | 등급별 기준내용.
/// </summary>
public sealed class PlanSubjectEdit : ObservableObject
{
    public const string DomainColumn = "영역";
    public const string AchievementColumn = "성취기준";

    private readonly GradeScale _scale;

    private string _name;
    public string Name { get => _name; set => SetProperty(ref _name, value); }

    public DataTable Grid { get; }
    public DataView GridView => Grid.DefaultView;

    public PlanSubjectEdit(string name, GradeScale scale, int seedEmptyRows = 6)
    {
        _scale = scale;
        _name = name;
        Grid = NewTable(scale);
        // 새 과목은 빈 셀 행을 미리 깔아 바로 입력·붙여넣기 가능하게 (저장 시 빈 행은 걸러짐)
        for (int i = 0; i < seedEmptyRows; i++) Grid.Rows.Add(Grid.NewRow());
    }

    public PlanSubjectEdit(SubjectPlan plan, GradeScale scale) : this(plan.SubjectName, scale, seedEmptyRows: 0)
    {
        foreach (var domain in plan.Domains)
        {
            var row = Grid.NewRow();
            row[DomainColumn] = domain;
            // 성취기준은 영역 내 등급들이 공유 — 첫 번째로 발견되는 값 사용
            row[AchievementColumn] = _scale.Levels
                .Select(l => plan.Criteria.TryGetValue((domain, l.Label), out var e) ? e.Achievement : null)
                .FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? "";
            foreach (var level in _scale.Levels)
                row[level.Label] = plan.Criteria.TryGetValue((domain, level.Label), out var e) ? e.Text : "";
            Grid.Rows.Add(row);
        }
    }

    private static DataTable NewTable(GradeScale scale)
    {
        var t = new DataTable();
        t.Columns.Add(DomainColumn);
        t.Columns.Add(AchievementColumn);
        foreach (var level in scale.Levels) t.Columns.Add(level.Label);
        return t;
    }

    /// <summary>표 → SubjectPlan. 영역명 중복이면 error 와 함께 null.</summary>
    public SubjectPlan? BuildPlan(out string? error)
    {
        var domains = new List<string>();
        var criteria = new Dictionary<(string, string), CriteriaEntry>();

        foreach (DataRow row in Grid.Rows)
        {
            var domain = (row[DomainColumn]?.ToString() ?? "").Trim();
            if (domain == "") continue;   // 영역 없는 행은 무시 (새 행 등)
            if (domains.Contains(domain)) { error = $"영역명 '{domain}'이(가) 중복됩니다."; return null; }
            domains.Add(domain);

            var ach = (row[AchievementColumn]?.ToString() ?? "").Trim();
            foreach (var level in _scale.Levels)
            {
                var text = (row[level.Label]?.ToString() ?? "").Trim();
                if (text != "")
                    criteria[(domain, level.Label)] = new CriteriaEntry(text, ach == "" ? null : ach);
            }
        }

        error = null;
        return new SubjectPlan(Name.Trim(), domains, criteria);
    }
}
