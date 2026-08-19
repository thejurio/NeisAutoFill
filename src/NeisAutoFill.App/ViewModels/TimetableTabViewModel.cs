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

/// <summary>주별 목록 한 줄.</summary>
public sealed class WeekRow
{
    public WeekRow(int number, DateOnly start, IReadOnlyList<TimetableSourceLesson> lessons,
        IReadOnlyDictionary<DateOnly, string> holidays)
    {
        Number = $"{number}주";
        Span = $"{start:MM-dd} ~ {start.AddDays(4):MM-dd}";
        Count = lessons.Count;

        // "국4 수3 영2" — 그 주에 무슨 수업이 몇 시간 있는지 한눈에
        Subjects = string.Join("  ", lessons
            .GroupBy(l => TimetableHourSummary.RowNameOf(l.SourceToken))
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key} {g.Count()}"));

        Holiday = string.Join(", ", holidays
            .Where(h => h.Key >= start && h.Key <= start.AddDays(6))
            .OrderBy(h => h.Key)
            .Select(h => $"{h.Key:M/d} {h.Value}"));
    }

    public string Number { get; }
    public string Span { get; }
    public int Count { get; }
    public string Subjects { get; }
    public string Holiday { get; }
}

/// <summary>시수 표 한 줄. 기준은 교사가 고칠 수 있다.</summary>
public sealed class HourRow : ObservableObject
{
    private readonly Action<string, int?> _onStandardChanged;

    public HourRow(SubjectHourRow row, Action<string, int?> onStandardChanged)
    {
        _onStandardChanged = onStandardChanged;
        Subject = row.Subject;
        Assigned = row.Assigned;
        _standard = row.Standard;
        IsEdited = row.StandardIsEdited;
    }

    public string Subject { get; }
    public int Assigned { get; }
    public bool IsEdited { get; }

    /// <summary>기준 시수. 비우면 비교하지 않는다.</summary>
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

    /// <summary>기준보다 모자란다 — 채워야 한다.</summary>
    public bool IsShort => _standard is not null && Assigned < _standard;

    /// <summary>기준을 넘겼다.</summary>
    public bool IsOver => _standard is not null && Assigned > _standard;
}

/// <summary>
/// 시간표 탭 — 문서 읽기부터 나이스 입력까지 <b>이 탭 안에서</b> 끝낸다.
///
/// 화면에 보이는 것이 곧 검토 자료다: 주별로 무슨 수업이 몇 시간 있는지,
/// 과목별 배정이 기준 시수와 얼마나 차이 나는지.
/// </summary>
public sealed class TimetableTabViewModel : ObservableObject
{
    private readonly TimetableSession _session;
    private readonly Action<string> _log;
    private readonly IProgress<ProgressInfo>? _progress;
    private readonly Func<bool> _isConnected;

    /// <summary>교사가 고친 기준 시수 (과목 → 시수). 문서 값보다 우선한다.</summary>
    private readonly Dictionary<string, int> _standardEdits = new();

    private TimetableSourcePackage? _source;
    private CreativeSourcePackage? _creative;

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
        ReviewCommand = new RelayCommand(OpenReview, () => _source is not null);
        RunCommand = new AsyncRelayCommand(RunAsync, () => CanRun);
    }

    public RelayCommand LoadTimetableCommand { get; }
    public RelayCommand LoadCreativeCommand { get; }
    public RelayCommand ReviewCommand { get; }
    public AsyncRelayCommand RunCommand { get; }

    public ObservableCollection<WeekRow> Weeks { get; } = new();
    public ObservableCollection<HourRow> Hours { get; } = new();
    public ObservableCollection<TimetableSemesterPart> Semesters { get; } = new();

    // ── 파일 ────────────────────────────────────────────────

    public string TimetableFileName
    {
        get => _timetableFileName;
        private set => SetProperty(ref _timetableFileName, value);
    }
    private string _timetableFileName = "선택된 파일 없음";

    public string CreativeFileName
    {
        get => _creativeFileName;
        private set => SetProperty(ref _creativeFileName, value);
    }
    private string _creativeFileName = "없음 (선택 사항)";

    // ── 학기 ────────────────────────────────────────────────

    public TimetableSemesterPart? SelectedSemester
    {
        get => _selectedSemester;
        set
        {
            if (!SetProperty(ref _selectedSemester, value) || value is null) return;
            From = value.Start;
            To = value.End;
            Refresh();
        }
    }
    private TimetableSemesterPart? _selectedSemester;

    // ── 기간 ────────────────────────────────────────────────

    public DateTime? FromDate
    {
        get => From?.ToDateTime(TimeOnly.MinValue);
        set { From = value is null ? null : DateOnly.FromDateTime(value.Value); Refresh(); }
    }

    public DateTime? ToDate
    {
        get => To?.ToDateTime(TimeOnly.MinValue);
        set { To = value is null ? null : DateOnly.FromDateTime(value.Value); Refresh(); }
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

    // ── 요약 ────────────────────────────────────────────────

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
    private string _summary = "시간표 파일을 선택하면 주별 수업과 과목별 시수를 보여 드립니다.";

    public string HourNote
    {
        get => _hourNote;
        private set => SetProperty(ref _hourNote, value);
    }
    private string _hourNote = "";

    public bool HasSource => _source is not null;

    public bool CanRun => _source is not null && SelectedLessons.Count > 0 && _isConnected();

    /// <summary>고른 기간 안의 수업.</summary>
    private IReadOnlyList<TimetableSourceLesson> SelectedLessons =>
        _source is null || From is null || To is null
            ? Array.Empty<TimetableSourceLesson>()
            : _source.Lessons.Where(l => l.Cell.Date >= From && l.Cell.Date <= To).ToList();

    /// <summary>연결 상태가 바뀌면 실행 버튼을 다시 판정한다.</summary>
    public void RefreshRunnable()
    {
        OnPropertyChanged(nameof(CanRun));
        RunCommand.RaiseCanExecuteChanged();
    }

    // ── 파일 읽기 ───────────────────────────────────────────

    private void LoadTimetable()
    {
        var path = AskFile("연간 시간표 파일 선택");
        if (path is null) return;

        try
        {
            _source = TimetableDocumentParser.Parse(PdfLayoutExtractor.ExtractAny(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"문서를 읽지 못했습니다.\n{ex.Message}", "시간표",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_source.Lessons.Count == 0)
        {
            _source = null;
            MessageBox.Show("문서에서 수업을 하나도 찾지 못했습니다.\nPDF 로 저장해 다시 넣어 보세요.",
                "시간표", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        TimetableFileName = Path.GetFileName(path);
        _standardEdits.Clear();

        Semesters.Clear();
        foreach (var part in _source.SemesterParts) Semesters.Add(part);

        _log($"시간표 문서: 수업 {_source.Lessons.Count}칸 · 공휴일 {_source.HolidayNames.Count}일 · " +
             $"경고 {_source.Warnings.Count}건");

        // 오늘이 든 학기를 먼저 고른다 — 대개 지금 넣으려는 학기다
        var today = DateOnly.FromDateTime(DateTime.Today);
        SelectedSemester = Semesters.FirstOrDefault(p => p.Contains(today)) ?? Semesters.LastOrDefault();

        if (SelectedSemester is null) Refresh();   // 학기 구간이 없으면 전체로
        OnPropertyChanged(nameof(HasSource));
        ReviewCommand.RaiseCanExecuteChanged();
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
            MessageBox.Show($"문서를 읽지 못했습니다.\n{ex.Message}", "창의적 체험활동",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CreativeFileName = $"{Path.GetFileName(path)} ({_creative.Events.Count}건)";
        _log($"창체 문서: {_creative.Events.Count}건");
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

    // ── 표 갱신 ─────────────────────────────────────────────

    private void Refresh()
    {
        Weeks.Clear();
        Hours.Clear();

        if (_source is null) return;

        var lessons = SelectedLessons;
        var semester = SelectedSemester?.Semester ?? 0;

        var weekStarts = lessons.Select(l => l.Cell.WeekStart).Distinct().OrderBy(d => d).ToList();
        for (var i = 0; i < weekStarts.Count; i++)
        {
            var start = weekStarts[i];
            Weeks.Add(new WeekRow(i + 1, start,
                lessons.Where(l => l.Cell.WeekStart == start).ToList(), _source.HolidayNames));
        }

        foreach (var row in TimetableHourSummary.Build(
                     lessons, _source.HourStandards, semester, _standardEdits))
            Hours.Add(new HourRow(row, OnStandardEdited));

        var gap = Hours.Count(h => h.DifferenceText.Length > 0 && h.DifferenceText != "0");

        Summary = $"수업 {lessons.Count}칸 · 주 {Weeks.Count}개 · 공휴일 " +
                  $"{_source.HolidayNames.Count(h => From is not null && To is not null && h.Key >= From && h.Key <= To)}일" +
                  (_source.Warnings.Count > 0 ? $" · 경고 {_source.Warnings.Count}건" : "");

        HourNote = _source.HourStandards.Count == 0
            ? "문서에서 시수표를 찾지 못했습니다. 기준 시수를 직접 입력하면 차이를 계산합니다."
            : gap == 0
                ? "모든 과목이 기준 시수와 맞습니다."
                : $"기준과 다른 과목 {gap}개 — 기준 칸을 눌러 고칠 수 있습니다.";

        RefreshRunnable();
    }

    private void OnStandardEdited(string subject, int? value)
    {
        if (value is null) _standardEdits.Remove(subject);
        else _standardEdits[subject] = value.Value;

        var gap = Hours.Count(h => h.DifferenceText.Length > 0 && h.DifferenceText != "0");
        HourNote = gap == 0 ? "모든 과목이 기준 시수와 맞습니다." : $"기준과 다른 과목 {gap}개";
    }

    // ── 검토 창 ─────────────────────────────────────────────

    private void OpenReview()
    {
        if (_source is null) return;

        var links = Array.Empty<CreativeLink>() as IReadOnlyList<CreativeLink>;
        var problems = Array.Empty<string>() as IReadOnlyList<string>;

        if (_creative is not null)
        {
            var merged = CreativeActivityMerger.Merge(_creative.Events);
            links = CreativeActivityLinker.Link(_source.Lessons, merged.Merged);
            problems = CreativeActivityLinker.CheckPair(_source, _creative);
        }

        var reviewed = TimetableReviewWindow.Ask(
            new TimetableReviewViewModel(_source, links, problems), Application.Current.MainWindow);

        if (reviewed is null) return;

        _source = _source with { Lessons = reviewed };
        _log($"검토에서 {reviewed.Count}칸으로 확정했습니다.");
        Refresh();
    }

    // ── 실행 ────────────────────────────────────────────────

    private async Task RunAsync()
    {
        if (_source is null || From is null || To is null) return;

        var range = new TimetableRangeChoice(From.Value, To.Value, AllowOverwrite);

        try
        {
            var flow = new Helpers.TimetableFlow(_session, _log);
            var result = await flow.RunLessonsAsync(
                SelectedLessons, range, Application.Current.MainWindow, _progress);

            _log(result.Message);
            MessageBox.Show(result.Message, "연간 시간표", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log($"시간표 오류: {ex.Message}");
            MessageBox.Show(ex.Message, "연간 시간표", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
