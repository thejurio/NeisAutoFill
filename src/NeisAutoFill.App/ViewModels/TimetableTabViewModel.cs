using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.App.Services;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Timetable;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.ViewModels;

/// <summary>시간표 격자의 칸 하나.</summary>
public sealed class TimetableCellVm : ObservableObject
{
    public TimetableCellVm(TimetableCell cell) => Cell = cell;

    public TimetableCell Cell { get; }

    /// <summary>원본 표기 ("국"). 수업이 없으면 빈 문자열.</summary>
    public string Token { get; set; } = "";

    /// <summary>보여 줄 과목명 — <b>줄임말이 아니라 정식 이름</b>("국"이 아니라 "국어").</summary>
    public string Subject
    {
        get => _subject;
        set => SetProperty(ref _subject, value);
    }
    private string _subject = "";

    public string Teacher
    {
        get => _teacher;
        set => SetProperty(ref _teacher, value);
    }
    private string _teacher = "";

    /// <summary>쉬는 날 — 입력하지 않는다. 연한 빨강으로 칠하고 무슨 날인지 적는다.</summary>
    public bool IsHoliday
    {
        get => _isHoliday;
        set => SetProperty(ref _isHoliday, value);
    }
    private bool _isHoliday;

    /// <summary>"설날" · "재량휴업일" 등.</summary>
    public string HolidayName
    {
        get => _holidayName;
        set => SetProperty(ref _holidayName, value);
    }
    private string _holidayName = "";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
    private bool _isSelected;

    /// <summary>교사가 아직 안 정해진 수업 — 이대로면 실행이 막힌다.</summary>
    public bool NeedsTeacher => !IsHoliday && Token.Length > 0 && Teacher.Length == 0;

    public bool HasLesson => !IsHoliday && Token.Length > 0;

    public void Refresh()
    {
        OnPropertyChanged(nameof(NeedsTeacher));
        OnPropertyChanged(nameof(HasLesson));
    }
}

/// <summary>격자의 한 행 = 한 교시.</summary>
public sealed class TimetableGridRow
{
    public TimetableGridRow(int period, IReadOnlyList<TimetableCellVm> days)
    {
        Period = $"{period}교시";
        Days = days;
    }

    public string Period { get; }
    public IReadOnlyList<TimetableCellVm> Days { get; }

    public TimetableCellVm? Mon => Days.ElementAtOrDefault(0);
    public TimetableCellVm? Tue => Days.ElementAtOrDefault(1);
    public TimetableCellVm? Wed => Days.ElementAtOrDefault(2);
    public TimetableCellVm? Thu => Days.ElementAtOrDefault(3);
    public TimetableCellVm? Fri => Days.ElementAtOrDefault(4);
}

/// <summary>
/// 교사 드롭다운의 한 항목.
/// <b>"입력 안 함"도 선택지여야 한다</b> — 나이스에 없는 활동(봉사활동 등)은
/// 교사를 정할 방법이 없어, 이 선택지가 없으면 실행이 영영 막힌다(실측 확인).
/// </summary>
public sealed record TeacherChoice(NeisTimetableOption? Option, string Label, bool IsMatch = false)
{
    public static TeacherChoice Skip { get; } = new(null, "— 입력 안 함 —");

    /// <summary>이름이 맞는 항목 — 교사 이름만 보여 준다.</summary>
    public static TeacherChoice Of(NeisTimetableOption option) =>
        new(option, option.TeacherName.Length > 0 ? option.TeacherName : option.Subject, IsMatch: true);

    /// <summary>이름이 다른 항목 — 어느 과목인지 함께 보여 줘야 잘못 고르지 않는다.</summary>
    public static TeacherChoice Other(NeisTimetableOption option) =>
        new(option, option.TeacherName.Length > 0
            ? $"{option.Subject} · {option.TeacherName}"
            : option.Subject);

    public bool IsSkip => Option is null;

    /// <summary>이 선택지가 가리키는 규칙 대상.</summary>
    public string TargetKey => Option?.StableKey ?? TimetableMappingRule.SkipKey;
}

/// <summary>과목 한 줄 — 담당 교사와 시수를 함께 본다.</summary>
public sealed class SubjectAssignmentRow : ObservableObject
{
    private readonly Action<SubjectAssignmentRow> _onTeacherChanged;
    private readonly Action<string, int?> _onStandardChanged;

    public SubjectAssignmentRow(
        string subject, IReadOnlyList<string> tokens, IReadOnlyList<TeacherChoice> candidates,
        int assigned, int? standard,
        Action<SubjectAssignmentRow> onTeacherChanged, Action<string, int?> onStandardChanged)
    {
        Subject = subject;
        Tokens = tokens;
        Candidates = candidates;
        Assigned = assigned;
        _standard = standard;
        _onTeacherChanged = onTeacherChanged;
        _onStandardChanged = onStandardChanged;
    }

    /// <summary>표준 과목명 (국어·자율·자치활동 …).</summary>
    public string Subject { get; }

    /// <summary>이 과목으로 정규화되는 원본 표기들 ("국", "국어"). 규칙은 원본 표기로 만든다.</summary>
    public IReadOnlyList<string> Tokens { get; }

    /// <summary>고를 수 있는 항목 (같은 과목에 교사가 여럿일 수 있다) + "입력 안 함".</summary>
    public IReadOnlyList<TeacherChoice> Candidates { get; }

    public int Assigned { get; }

    /// <summary>기본 담당 교사. 여기서 고른 것이 그 과목 전체의 기본값이 된다.</summary>
    public TeacherChoice? Teacher
    {
        get => _teacher;
        set
        {
            if (!SetProperty(ref _teacher, value)) return;
            _onTeacherChanged(this);
            OnPropertyChanged(nameof(NeedsTeacher));
        }
    }
    private TeacherChoice? _teacher;

    /// <summary>나이스에 같은 이름의 과목이 없다 — 다른 과목으로 잇거나 [입력 안 함]을 골라야 한다.</summary>
    public bool HasNoMatch => !Candidates.Any(c => c.IsMatch);

    /// <summary>아직 아무것도 안 골랐다. "입력 안 함"을 골랐으면 정한 것이다.</summary>
    public bool NeedsTeacher => _teacher is null;

    public int? Standard
    {
        get => _standard;
        set
        {
            if (!SetProperty(ref _standard, value)) return;
            _onStandardChanged(Subject, value);
            OnPropertyChanged(nameof(DifferenceText));
            OnPropertyChanged(nameof(IsShort));
            OnPropertyChanged(nameof(IsOver));
        }
    }
    private int? _standard;

    public string DifferenceText => _standard is null ? "" : (Assigned - _standard.Value).ToString("+#;-#;0");
    public bool IsShort => _standard is not null && Assigned < _standard;
    public bool IsOver => _standard is not null && Assigned > _standard;
}

/// <summary>예외 규칙 한 줄 (정기·비정기 공통).</summary>
public sealed record ExceptionRow(TimetableMappingRule Rule, string Text);

/// <summary>
/// 시간표 탭 — 문서 읽기부터 나이스 입력까지 <b>이 탭 안에서</b> 끝낸다.
///
/// 교사 배정은 세 겹이다 (기술설계 §5 MappingScope 를 그대로 쓴다):
/// <list type="number">
/// <item><b>기본</b> — 과목마다 담당 교사 한 명</item>
/// <item><b>정기 예외</b> — "월요일 3교시 국어만 B선생님"</item>
/// <item><b>비정기 예외</b> — "9월 15일 3교시만 C선생님"</item>
/// </list>
/// 더 구체적인 규칙이 이긴다.
/// </summary>
public sealed class TimetableTabViewModel : ObservableObject
{
    private readonly TimetableSession _session;
    private readonly Action<string> _log;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly Func<bool> _isConnected;

    private readonly Dictionary<string, int> _standardEdits = new();
    private readonly Dictionary<DateOnly, string> _extraHolidays = new();   // 사용자가 추가한 쉬는 날
    private readonly List<TimetableMappingRule> _rules = new();

    /// <summary>편집 가능한 수업 목록 — 칸의 과목을 바꾸거나 넣을 수 있어야 한다.</summary>
    private readonly List<TimetableSourceLesson> _lessons = new();

    private TimetableSourcePackage? _source;
    private CreativeSourcePackage? _creative;
    private TimetableCatalog? _catalog;
    private bool _suspendRefresh;

    public TimetableTabViewModel(
        TimetableSession session, Action<string> log,
        IProgress<ProgressInfo>? progress, Func<bool> isConnected)
    {
        _session = session;
        _log = log;
        _progress = progress;
        _isConnected = isConnected;

        LoadTimetableCommand = new RelayCommand(LoadTimetable);
        LoadCreativeCommand = new RelayCommand(LoadCreative);
        LoadCatalogCommand = new AsyncRelayCommand(LoadCatalogAsync, () => _isConnected());
        PreviousWeekCommand = new RelayCommand(() => MoveWeek(-1), () => WeekIndex > 0);
        NextWeekCommand = new RelayCommand(() => MoveWeek(1), () => WeekIndex < Weeks.Count - 1);
        SelectCellCommand = new RelayCommand<TimetableCellVm>(SelectCell);
        ApplyOnceCommand = new RelayCommand(() => ApplyTeacher(regular: false), () => CanApplyTeacher);
        ApplyRegularCommand = new RelayCommand(() => ApplyTeacher(regular: true), () => CanApplyTeacher);
        MakeHolidayCommand = new RelayCommand(MakeHoliday, () => SelectedCell is not null);
        ClearHolidayCommand = new RelayCommand(ClearHoliday, () => SelectedCell?.IsHoliday == true);
        RemoveExceptionCommand = new RelayCommand<ExceptionRow>(RemoveException);
        ChangeSubjectCommand = new RelayCommand(ChangeSubject, () => SelectedCell is not null && SelectedSubject is not null);
        ClearLessonCommand = new RelayCommand(ClearLesson, () => SelectedCell?.HasLesson == true);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
    }

    public RelayCommand LoadTimetableCommand { get; }
    public RelayCommand LoadCreativeCommand { get; }
    public AsyncRelayCommand LoadCatalogCommand { get; }
    public RelayCommand PreviousWeekCommand { get; }
    public RelayCommand NextWeekCommand { get; }
    public RelayCommand<TimetableCellVm> SelectCellCommand { get; }
    public RelayCommand ApplyOnceCommand { get; }
    public RelayCommand ApplyRegularCommand { get; }
    public RelayCommand MakeHolidayCommand { get; }
    public RelayCommand ClearHolidayCommand { get; }
    public RelayCommand<ExceptionRow> RemoveExceptionCommand { get; }
    public RelayCommand ChangeSubjectCommand { get; }
    public RelayCommand ClearLessonCommand { get; }
    public AsyncRelayCommand RunCommand { get; }

    public ObservableCollection<TimetableGridRow> Grid { get; } = new();
    public ObservableCollection<SubjectAssignmentRow> Subjects { get; } = new();
    public ObservableCollection<ExceptionRow> Exceptions { get; } = new();
    public ObservableCollection<TimetableSemesterPart> Semesters { get; } = new();

    private readonly List<DateOnly> Weeks = new();

    // ── 자료 ────────────────────────────────────────────────

    public string TimetableFileName
    {
        get => _timetableFileName;
        private set => SetProperty(ref _timetableFileName, value);
    }
    private string _timetableFileName = "파일 없음";

    public string CreativeFileName
    {
        get => _creativeFileName;
        private set => SetProperty(ref _creativeFileName, value);
    }
    private string _creativeFileName = "없음";

    public string CatalogState
    {
        get => _catalogState;
        private set => SetProperty(ref _catalogState, value);
    }
    private string _catalogState = "안 불러옴";

    public bool HasSource => _source is not null;
    public bool HasCatalog => _catalog is not null;

    // ── 범위 ────────────────────────────────────────────────

    public TimetableSemesterPart? SelectedSemester
    {
        get => _selectedSemester;
        set
        {
            if (!SetProperty(ref _selectedSemester, value) || value is null) return;
            _suspendRefresh = true;
            From = value.Start;
            To = value.End;
            _suspendRefresh = false;
            Rebuild();
        }
    }
    private TimetableSemesterPart? _selectedSemester;

    public DateTime? FromDate
    {
        get => _from?.ToDateTime(TimeOnly.MinValue);
        set { From = value is null ? null : DateOnly.FromDateTime(value.Value); Rebuild(); }
    }

    public DateTime? ToDate
    {
        get => _to?.ToDateTime(TimeOnly.MinValue);
        set { To = value is null ? null : DateOnly.FromDateTime(value.Value); Rebuild(); }
    }

    private DateOnly? From
    {
        get => _from;
        set { _from = value; OnPropertyChanged(nameof(FromDate)); }
    }
    private DateOnly? _from;

    private DateOnly? To
    {
        get => _to;
        set { _to = value; OnPropertyChanged(nameof(ToDate)); }
    }
    private DateOnly? _to;

    public bool AllowOverwrite
    {
        get => _allowOverwrite;
        set => SetProperty(ref _allowOverwrite, value);
    }
    private bool _allowOverwrite = true;

    // ── 주 이동 ─────────────────────────────────────────────

    public int WeekIndex
    {
        get => _weekIndex;
        set
        {
            if (!SetProperty(ref _weekIndex, value)) return;
            BuildGrid();
            OnPropertyChanged(nameof(WeekLabel));
            PreviousWeekCommand.RaiseCanExecuteChanged();
            NextWeekCommand.RaiseCanExecuteChanged();
        }
    }
    private int _weekIndex;

    public string WeekLabel => Weeks.Count == 0 || WeekIndex >= Weeks.Count
        ? ""
        : $"{WeekIndex + 1}주 / {Weeks.Count}주   {Weeks[WeekIndex]:MM-dd} ~ {Weeks[WeekIndex].AddDays(4):MM-dd}";

    private void MoveWeek(int delta) => WeekIndex = Math.Clamp(WeekIndex + delta, 0, Math.Max(0, Weeks.Count - 1));

    // ── 선택한 칸 ───────────────────────────────────────────

    public TimetableCellVm? SelectedCell
    {
        get => _selectedCell;
        private set
        {
            if (_selectedCell is not null) _selectedCell.IsSelected = false;
            SetProperty(ref _selectedCell, value);
            if (value is not null) value.IsSelected = true;

            OnPropertyChanged(nameof(SelectedCellLabel));
            OnPropertyChanged(nameof(HasSelectedCell));
            OnPropertyChanged(nameof(SelectedCandidates));
            OnPropertyChanged(nameof(RegularLabel));
            SelectedTeacher = null;
            _selectedSubject = value?.Subject;
            OnPropertyChanged(nameof(SelectedSubject));
            OnPropertyChanged(nameof(SubjectChoices));

            MakeHolidayCommand.RaiseCanExecuteChanged();
            ClearHolidayCommand.RaiseCanExecuteChanged();
            ChangeSubjectCommand.RaiseCanExecuteChanged();
            ClearLessonCommand.RaiseCanExecuteChanged();
        }
    }
    private TimetableCellVm? _selectedCell;

    public bool HasSelectedCell => _selectedCell is not null;

    public string SelectedCellLabel => _selectedCell is null
        ? "격자에서 칸을 고르세요."
        : $"{_selectedCell.Cell.Date:MM-dd}({Korean(_selectedCell.Cell.DayOfWeek)}) {_selectedCell.Cell.Period}교시" +
          (_selectedCell.IsHoliday ? $" · {_selectedCell.HolidayName}"
           : _selectedCell.Subject.Length > 0 ? $" · {_selectedCell.Subject}" : " · 수업 없음");

    /// <summary>"월요일 3교시 전부" — 정기 예외 버튼에 그대로 쓴다.</summary>
    public string RegularLabel => _selectedCell is null
        ? "이 요일·교시 전부"
        : $"{Korean(_selectedCell.Cell.DayOfWeek)}요일 {_selectedCell.Cell.Period}교시 전부";

    /// <summary>선택한 칸의 과목으로 고를 수 있는 교사들.</summary>
    public IReadOnlyList<TeacherChoice> SelectedCandidates =>
        _catalog is null || _selectedCell is null || !_selectedCell.HasLesson
            ? Array.Empty<TeacherChoice>()
            : CandidatesFor(_selectedCell.Token);

    /// <summary>
    /// 고를 수 있는 과목 — <b>나이스 목록의 정식 이름</b>만 보여 준다.
    /// "국"처럼 줄여 쓰면 잘못 고르기 쉬워서 드롭다운으로만 고르게 한다.
    /// </summary>
    public IReadOnlyList<string> SubjectChoices =>
        _catalog is null
            ? Array.Empty<string>()
            : _catalog.Assignable.Select(o => o.Subject).Distinct()
                .OrderBy(x => x, StringComparer.CurrentCulture).ToList();

    public string? SelectedSubject
    {
        get => _selectedSubject;
        set
        {
            if (!SetProperty(ref _selectedSubject, value)) return;
            ChangeSubjectCommand.RaiseCanExecuteChanged();
        }
    }
    private string? _selectedSubject;

    public TeacherChoice? SelectedTeacher
    {
        get => _selectedTeacher;
        set
        {
            if (!SetProperty(ref _selectedTeacher, value)) return;
            OnPropertyChanged(nameof(CanApplyTeacher));
            ApplyOnceCommand.RaiseCanExecuteChanged();
            ApplyRegularCommand.RaiseCanExecuteChanged();
        }
    }
    private TeacherChoice? _selectedTeacher;

    public bool CanApplyTeacher => _selectedCell?.HasLesson == true && _selectedTeacher is not null;

    // ── 요약 ────────────────────────────────────────────────

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
    private string _summary = "시간표 파일을 고르면 여기에 표시됩니다.";

    public string Warning
    {
        get => _warning;
        private set { SetProperty(ref _warning, value); OnPropertyChanged(nameof(HasWarning)); }
    }
    private string _warning = "";

    public bool HasWarning => _warning.Length > 0;

    public bool CanRun => _source is not null && _catalog is not null
                          && Lessons.Count > 0 && _isConnected()
                          && Subjects.All(s => !s.NeedsTeacher);

    /// <summary>고른 기간 안의, 쉬는 날을 뺀 수업.</summary>
    private IReadOnlyList<TimetableSourceLesson> Lessons =>
        _source is null || _from is null || _to is null
            ? Array.Empty<TimetableSourceLesson>()
            : _lessons
                .Where(l => l.Cell.Date >= _from && l.Cell.Date <= _to)
                .Where(l => !IsHoliday(l.Cell.Date))
                .ToList();

    private bool IsHoliday(DateOnly date) =>
        _extraHolidays.ContainsKey(date) || (_source?.HolidayNames.ContainsKey(date) ?? false);

    private string HolidayNameOf(DateOnly date) =>
        _extraHolidays.TryGetValue(date, out var mine) ? mine
        : _source is not null && _source.HolidayNames.TryGetValue(date, out var doc) ? doc
        : "";

    public void RefreshRunnable()
    {
        OnPropertyChanged(nameof(CanRun));
        RunCommand.RaiseCanExecuteChanged();
        LoadCatalogCommand.RaiseCanExecuteChanged();
    }

    // ── 자료 읽기 ───────────────────────────────────────────

    private void LoadTimetable()
    {
        var path = AskFile("연간 시간표 파일 선택");
        if (path is null) return;

        TimetableSourcePackage parsed;
        try
        {
            parsed = TimetableDocumentParser.Parse(PdfLayoutExtractor.ExtractAny(path));
        }
        catch (Exception ex)
        {
            Warn($"문서를 읽지 못했습니다. {ex.Message}");
            return;
        }

        if (parsed.Lessons.Count == 0)
        {
            Warn("문서에서 수업을 하나도 찾지 못했습니다. PDF 로 저장해 다시 넣어 보세요.");
            return;
        }

        _source = parsed;
        _lessons.Clear();
        _lessons.AddRange(parsed.Lessons);
        _standardEdits.Clear();
        _extraHolidays.Clear();
        TimetableFileName = Path.GetFileName(path);
        // 교사 배정(_rules)은 지우지 않는다 — 같은 학급이면 문서를 다시 넣어도 그대로 쓴다

        Semesters.Clear();
        foreach (var part in parsed.SemesterParts) Semesters.Add(part);

        _log($"시간표 문서: 수업 {parsed.Lessons.Count}칸 · 공휴일 {parsed.HolidayNames.Count}일 · " +
             $"경고 {parsed.Warnings.Count}건");

        var today = DateOnly.FromDateTime(DateTime.Today);
        var pick = Semesters.FirstOrDefault(p => p.Contains(today)) ?? Semesters.LastOrDefault();

        OnPropertyChanged(nameof(HasSource));
        if (pick is not null) SelectedSemester = pick;
        else Rebuild();
    }

    private void LoadCreative()
    {
        var path = AskFile("창의적 체험활동 계획 선택");
        if (path is null) return;

        try
        {
            _creative = CreativeDocumentParser.Parse(PdfLayoutExtractor.ExtractAny(path));
        }
        catch (Exception ex)
        {
            Warn($"문서를 읽지 못했습니다. {ex.Message}");
            return;
        }

        CreativeFileName = $"{Path.GetFileName(path)} · {_creative.Events.Count}건";
        ApplyCreativeLinks();
    }

    /// <summary>
    /// 창체 계획으로 '창'처럼 종류가 안 정해진 칸을 자율·동아리·진로로 바꾼다.
    /// 계획을 넣고도 종류가 안 정해지면 그 칸은 실행에서 막히므로 몇 칸이 남았는지 알린다.
    /// </summary>
    private void ApplyCreativeLinks()
    {
        if (_creative is null || _source is null) return;

        var merged = CreativeActivityMerger.Merge(_creative.Events);
        var links = CreativeActivityLinker.Link(_lessons, merged.Merged);

        var changed = 0;
        foreach (var link in links.Where(l => l.IsResolved))
        {
            var index = _lessons.FindIndex(l => l.Cell == link.Cell);
            if (index < 0) continue;

            var token = link.Kind switch
            {
                CreativeActivityKind.Autonomy => "자",
                CreativeActivityKind.Club => "동",
                CreativeActivityKind.Career => "진",
                _ => null,
            };
            if (token is null || _lessons[index].SourceToken == token) continue;

            _lessons[index] = _lessons[index] with { SourceToken = token };
            changed++;
        }

        var unresolved = links.Count(l => !l.IsResolved);
        _log($"창체 {_creative.Events.Count}건 → 병합 {merged.Merged.Count}건 · " +
             $"칸 {changed}개 종류 확정 · 남은 미분류 {unresolved}칸");

        Rebuild();
    }

    /// <summary>나이스에서 과목·교사 목록을 읽는다 — 교사를 배정하려면 이것이 먼저다.</summary>
    private async Task LoadCatalogAsync()
    {
        var target = _from ?? DateOnly.FromDateTime(DateTime.Today);

        var pre = await _session.PreflightAsync(target, _progress);
        if (!pre.Ok)
        {
            CatalogState = "실패";
            Warn(pre.Message);
            return;
        }

        _catalog = _session.Catalog;
        CatalogState = _catalog is null ? "실패" : $"과목·교사 {_catalog.Assignable.Count}개";
        _log(pre.Message);

        // 지난번에 정해 둔 배정을 되살린다 — 매번 다시 고르게 하면 쓸 수가 없다
        if (_catalog is not null && _rules.Count == 0)
        {
            var saved = _session.LoadRules(out var catalogChanged);
            if (saved.Count > 0)
            {
                _rules.AddRange(saved);
                _log(catalogChanged
                    ? $"저장된 배정 {saved.Count}건을 되살렸습니다 — 나이스 목록이 바뀌어 일부는 다시 정해야 합니다."
                    : $"저장된 배정 {saved.Count}건을 되살렸습니다.");
            }
        }

        OnPropertyChanged(nameof(HasCatalog));
        Rebuild();
    }

    private static string? AskFile(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "시간표 문서|*.pdf;*.hwp;*.hwpx|PDF|*.pdf|한글|*.hwp;*.hwpx|모든 파일|*.*",
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void Warn(string message)
    {
        Warning = message;
        _log(message);
    }

    // ── 다시 그리기 ─────────────────────────────────────────

    private void Rebuild()
    {
        if (_suspendRefresh || _source is null) return;

        var lessons = Lessons;

        Weeks.Clear();
        Weeks.AddRange(lessons.Select(l => l.Cell.WeekStart).Distinct().OrderBy(d => d));
        _weekIndex = Math.Clamp(_weekIndex, 0, Math.Max(0, Weeks.Count - 1));

        BuildSubjects(lessons);
        BuildGrid();
        BuildExceptions();

        var holidayCount = AllHolidayDates().Count(d => _from is not null && _to is not null && d >= _from && d <= _to);
        Summary = $"수업 {lessons.Count}칸 · 주 {Weeks.Count}개 · 쉬는 날 {holidayCount}일" +
                  (_source.Warnings.Count > 0 ? $" · 문서 경고 {_source.Warnings.Count}건" : "");

        Warning = BuildWarning();

        OnPropertyChanged(nameof(WeekIndex));
        OnPropertyChanged(nameof(WeekLabel));
        PreviousWeekCommand.RaiseCanExecuteChanged();
        NextWeekCommand.RaiseCanExecuteChanged();
        RefreshRunnable();
    }

    /// <summary>지금 무엇이 막고 있는지 한 줄로. 막는 게 없으면 빈 문자열.</summary>
    private string BuildWarning()
    {
        if (_catalog is null)
            return "먼저 [나이스에서 과목·교사 불러오기]를 눌러 주세요. 교사를 배정해야 입력할 수 있습니다.";

        var missing = Subjects.Where(x => x.NeedsTeacher).ToList();
        if (missing.Count == 0) return "";

        // 나이스에 아예 없는 활동은 "고르지 않은 것"이 아니라 "고를 수 없는 것"이다 — 다르게 안내한다
        var noMatch = missing.Where(x => x.HasNoMatch).Select(x => x.Subject).ToList();
        var unpicked = missing.Where(x => !x.HasNoMatch).Select(x => x.Subject).ToList();

        var parts = new List<string>();
        if (unpicked.Count > 0) parts.Add($"담당 교사를 정하지 않은 과목: {string.Join(", ", unpicked)}");
        if (noMatch.Count > 0)
            parts.Add($"나이스에 같은 이름이 없는 과목: {string.Join(", ", noMatch)} — " +
                      "드롭다운에서 넣을 나이스 과목을 고르거나 [입력 안 함]을 고르세요");

        return string.Join("\n", parts);
    }

    private IEnumerable<DateOnly> AllHolidayDates() =>
        (_source?.HolidayNames.Keys ?? Enumerable.Empty<DateOnly>()).Concat(_extraHolidays.Keys).Distinct();

    /// <summary>과목별 담당 교사와 시수. 후보가 하나뿐이면 바로 정해 준다.</summary>
    private void BuildSubjects(IReadOnlyList<TimetableSourceLesson> lessons)
    {
        var previous = Subjects.ToDictionary(s => s.Subject, s => s.Teacher);
        Subjects.Clear();

        var semester = SelectedSemester?.Semester ?? 0;
        var standards = _source?.HourStandards ?? Array.Empty<SubjectHourStandard>();

        var groups = lessons
            .GroupBy(l => TimetableTokenNormalizer.Normalize(l.SourceToken).Standard)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in groups)
        {
            var tokens = group.Select(l => l.SourceToken).Distinct().ToList();
            var candidates = CandidatesFor(tokens[0]);

            // 기준 시수: 창체 계열은 문서의 '창체' 한 칸과 견준다
            var hourName = TimetableHourSummary.RowNameOf(tokens[0]);
            var standard = _standardEdits.TryGetValue(group.Key, out var edited)
                ? edited
                : hourName == TimetableHourSummary.CreativeRow
                    ? null   // 창체는 아래 합계 줄에서 견준다
                    : standards.FirstOrDefault(s => s.Subject == group.Key)?.For(semester);

            var row = new SubjectAssignmentRow(
                group.Key, tokens, candidates, group.Count(), standard, OnTeacherChanged, OnStandardEdited);

            // 순서: ① 저장·기존 규칙 ② 화면에서 방금 고른 것 ③ 후보가 딱 하나면 자동.
            // 여러 명 중 하나를 임의로 고르지는 않는다(D-002).
            var fromRule = _rules
                .FirstOrDefault(r => r.Scope.Kind == MappingScopeKind.Default && tokens.Contains(r.SourceToken));

            var matched = candidates.Where(c => c.IsMatch).ToList();

            row.Teacher =
                fromRule is not null && candidates.FirstOrDefault(c => c.TargetKey == fromRule.TargetStableKey) is { } saved
                    ? saved
                : previous.TryGetValue(group.Key, out var kept) && kept is not null
                  && candidates.FirstOrDefault(c => c.TargetKey == kept.TargetKey) is { } same
                    ? same
                : matched.Count == 1 ? matched[0] : null;

            Subjects.Add(row);
        }

        // 창체 합계 — 문서 시수표는 자율·동아리·진로를 한 칸으로 센다
        var creative = lessons.Count(l => TimetableHourSummary.RowNameOf(l.SourceToken) == TimetableHourSummary.CreativeRow);
        var creativeStandard = standards.FirstOrDefault(s => s.Subject == TimetableHourSummary.CreativeRow)?.For(semester);

        CreativeSummary = creative == 0 ? ""
            : creativeStandard is null
                ? $"창체 합계 배정 {creative}시간"
                : $"창체 합계 배정 {creative} · 기준 {creativeStandard} · 차이 {(creative - creativeStandard.Value):+#;-#;0}";
    }

    public string CreativeSummary
    {
        get => _creativeSummary;
        private set => SetProperty(ref _creativeSummary, value);
    }
    private string _creativeSummary = "";

    /// <summary>
    /// 그 원본 표기로 고를 수 있는 항목들. 끝에 언제나 "입력 안 함"을 붙인다 —
    /// 나이스에 없는 활동이라도 사용자가 매듭지을 수 있어야 실행이 막히지 않는다.
    /// </summary>
    private IReadOnlyList<TeacherChoice> CandidatesFor(string token)
    {
        if (_catalog is null) return new[] { TeacherChoice.Skip };

        var matched = MatchingOptions(token);

        var choices = new List<TeacherChoice>();
        choices.AddRange(matched.Select(TeacherChoice.Of));
        choices.Add(TeacherChoice.Skip);

        // 이름이 같은 항목 말고 <b>나이스의 다른 과목</b>도 고를 수 있어야 한다.
        // 문서는 "정보"인데 나이스는 "실과"처럼 이름이 어긋나는 일이 흔하고,
        // 그때 [입력 안 함]밖에 없으면 진짜 수업이 조용히 빠진다.
        var rest = _catalog.Assignable.Except(matched)
            .OrderBy(o => o.Subject, StringComparer.CurrentCulture)
            .ThenBy(o => o.TeacherName, StringComparer.CurrentCulture);

        choices.AddRange(rest.Select(TeacherChoice.Other));
        return choices;
    }

    /// <summary>이름(또는 창체 종류)이 맞는 나이스 항목들.</summary>
    private IReadOnlyList<NeisTimetableOption> MatchingOptions(string token)
    {
        if (_catalog is null) return Array.Empty<NeisTimetableOption>();

        var norm = TimetableTokenNormalizer.Normalize(token);

        // 창체는 종류(자율·동아리·진로)가 같은 것만 맞는 것으로 본다 — 자율 자리에 동아리를 넣으면 안 된다
        if (norm.CreativeKind != CreativeActivityKind.Unresolved)
            return _catalog.Assignable.Where(o => o.CreativeKind == norm.CreativeKind).ToList();

        var byName = _catalog.FindBySubject(norm.Standard);
        return byName.Count > 0 ? byName : _catalog.FindBySubject(norm.Raw);
    }

    private void BuildGrid()
    {
        Grid.Clear();
        if (_source is null || Weeks.Count == 0 || WeekIndex >= Weeks.Count) return;

        var start = Weeks[WeekIndex];
        var inWeek = _lessons.Where(l => l.Cell.WeekStart == start).ToList();
        var periods = inWeek.Select(l => l.Cell.Period).DefaultIfEmpty(6).Max();

        for (var period = 1; period <= periods; period++)
        {
            var days = new List<TimetableCellVm>();
            for (var d = 0; d < 5; d++)
            {
                var date = start.AddDays(d);
                var cell = new TimetableCellVm(new TimetableCell(date, period));

                if (IsHoliday(date))
                {
                    cell.IsHoliday = true;
                    cell.HolidayName = HolidayNameOf(date);
                }
                else
                {
                    var lesson = inWeek.FirstOrDefault(l => l.Cell.Date == date && l.Cell.Period == period);
                    if (lesson is not null)
                    {
                        cell.Token = lesson.SourceToken;
                        cell.Subject = TimetableTokenNormalizer.Normalize(lesson.SourceToken).Standard;
                        cell.Teacher = TeacherNameFor(cell.Cell, lesson.SourceToken);
                    }
                }

                cell.Refresh();
                days.Add(cell);
            }
            Grid.Add(new TimetableGridRow(period, days));
        }

        SelectedCell = null;
    }

    /// <summary>이 칸에 실제로 들어갈 교사 — 규칙을 풀어서 얻는다(가장 구체적인 규칙이 이긴다).</summary>
    private string TeacherNameFor(TimetableCell cell, string token)
    {
        if (_catalog is null) return "";

        var resolution = TimetableMappingResolver.Resolve(token, cell, _rules);

        if (resolution.Kind == MappingResolutionKind.Skip) return "입력 안 함";
        if (resolution.Kind != MappingResolutionKind.Resolved) return "";

        return _catalog.Find(resolution.TargetStableKey)?.TeacherName ?? "";
    }

    private void BuildExceptions()
    {
        Exceptions.Clear();
        if (_catalog is null) return;

        foreach (var rule in _rules.Where(r => r.Scope.Kind != MappingScopeKind.Default)
                     .OrderBy(r => r.Scope.Priority).ThenBy(r => r.SourceToken, StringComparer.Ordinal))
        {
            var subject = TimetableTokenNormalizer.Normalize(rule.SourceToken).Standard;
            var target = rule.IsSkip
                ? "입력 안 함"
                : _catalog.Find(rule.TargetStableKey)?.TeacherName ?? "?";

            Exceptions.Add(new ExceptionRow(rule, $"{rule.Scope.Description} · {subject} → {target}"));
        }
    }

    // ── 교사 배정 ───────────────────────────────────────────

    private void OnTeacherChanged(SubjectAssignmentRow row)
    {
        // 그 과목의 기본 규칙을 갈아 끼운다 (예외 규칙은 건드리지 않는다)
        _rules.RemoveAll(r => r.Scope.Kind == MappingScopeKind.Default && row.Tokens.Contains(r.SourceToken));

        if (row.Teacher is not null)
            foreach (var token in row.Tokens)
                _rules.Add(new TimetableMappingRule(token, row.Teacher.TargetKey, MappingScope.Default,
                    IsUserConfirmed: true, _catalog?.Fingerprint ?? ""));

        BuildGrid();
        Warning = BuildWarning();
        _session.SaveRules(_rules);
        RefreshRunnable();
    }

    private void OnStandardEdited(string subject, int? value)
    {
        if (value is null) _standardEdits.Remove(subject);
        else _standardEdits[subject] = value.Value;
    }

    private void SelectCell(TimetableCellVm? cell) => SelectedCell = cell;

    /// <param name="regular">true 면 요일+교시 전체(정기), false 면 그 날짜 한 칸만(비정기)</param>
    private void ApplyTeacher(bool regular)
    {
        if (_selectedCell is null || _selectedTeacher is null) return;

        var cell = _selectedCell.Cell;
        var token = _selectedCell.Token;
        var scope = regular
            ? MappingScope.ForDayPeriod(cell.DayOfWeek, cell.Period)
            : MappingScope.ForDate(cell.Date, cell.Period);

        _rules.RemoveAll(r => r.SourceToken == token && r.Scope == scope);
        _rules.Add(new TimetableMappingRule(token, _selectedTeacher.TargetKey, scope,
            IsUserConfirmed: true, _catalog?.Fingerprint ?? ""));

        _log($"예외 추가 — {scope.Description} · {_selectedCell.Subject}");

        _session.SaveRules(_rules);
        BuildGrid();
        BuildExceptions();
    }

    private void RemoveException(ExceptionRow? row)
    {
        if (row is null) return;

        _rules.Remove(row.Rule);
        _session.SaveRules(_rules);
        BuildGrid();
        BuildExceptions();
    }

    /// <summary>
    /// 선택한 칸의 과목을 바꾼다. 수업이 없던 칸이면 새로 넣는다.
    /// 문서를 잘못 읽었거나 학교 사정으로 과목이 바뀐 칸을 여기서 고친다.
    /// </summary>
    private void ChangeSubject()
    {
        if (_selectedCell is null || _selectedSubject is null) return;

        var cell = _selectedCell.Cell;
        var index = _lessons.FindIndex(l => l.Cell == cell);

        if (index >= 0) _lessons[index] = _lessons[index] with { SourceToken = _selectedSubject };
        else _lessons.Add(new TimetableSourceLesson(cell, _selectedSubject));

        _log($"{cell} 과목을 {_selectedSubject} 로 바꿨습니다.");
        Rebuild();
    }

    /// <summary>선택한 칸의 수업을 뺀다 (쉬는 날은 아니지만 그 칸은 비워 둔다).</summary>
    private void ClearLesson()
    {
        if (_selectedCell is null) return;

        var cell = _selectedCell.Cell;
        if (_lessons.RemoveAll(l => l.Cell == cell) > 0)
        {
            _log($"{cell} 수업을 뺐습니다.");
            Rebuild();
        }
    }

    // ── 쉬는 날 ─────────────────────────────────────────────

    private void MakeHoliday()
    {
        if (_selectedCell is null) return;

        var date = _selectedCell.Cell.Date;
        var name = HolidayPromptWindow.Ask(date, Application.Current.MainWindow);
        if (name is null) return;

        _extraHolidays[date] = name;
        _log($"쉬는 날 추가 — {date:yyyy-MM-dd} {name}");
        Rebuild();
    }

    private void ClearHoliday()
    {
        if (_selectedCell is null) return;

        var date = _selectedCell.Cell.Date;
        if (!_extraHolidays.Remove(date))
        {
            Warn($"{date:yyyy-MM-dd} 은 문서에 적힌 쉬는 날이라 여기서 되돌릴 수 없습니다.");
            return;
        }

        _log($"쉬는 날 해제 — {date:yyyy-MM-dd}");
        Rebuild();
    }

    // ── 실행 ────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_source is null || _from is null || _to is null) return;

        var range = new TimetableRangeChoice(_from.Value, _to.Value, AllowOverwrite);

        try
        {
            var flow = new Helpers.TimetableFlow(_session, _log);
            var result = await flow.RunPreparedAsync(
                Lessons, _rules, range, Application.Current.MainWindow, _progress);

            _log(result.Message);
            MessageBox.Show(result.Message, "연간 시간표", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Warn($"시간표 오류: {ex.Message}");
        }
    }

    private static string Korean(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "월",
        DayOfWeek.Tuesday => "화",
        DayOfWeek.Wednesday => "수",
        DayOfWeek.Thursday => "목",
        DayOfWeek.Friday => "금",
        DayOfWeek.Saturday => "토",
        _ => "일",
    };
}
