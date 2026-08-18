using System.Collections.ObjectModel;
using System.Windows.Input;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.ViewModels;

/// <summary>검토 표의 칸 하나. 사용자가 여기서 직접 고칠 수 있다.</summary>
public sealed class ReviewCell : ObservableObject
{
    public ReviewCell(DateOnly date, int period, string token, string note = "")
    {
        Date = date;
        Period = period;
        _token = token;
        Note = note;
    }

    /// <summary>날짜가 없는 자리(그 요일이 학기 밖) — 편집할 수 없다.</summary>
    public static ReviewCell Empty(int period) => new(default, period, "") { IsPlaceholder = true };

    public DateOnly Date { get; }
    public int Period { get; }
    public bool IsPlaceholder { get; private init; }

    private string _token;
    /// <summary>원본 표기. 비우면 그 칸은 입력 대상에서 빠진다.</summary>
    public string Token
    {
        get => _token;
        set
        {
            if (!SetProperty(ref _token, value?.Trim() ?? "")) return;
            Edited = true;
            OnPropertyChanged(nameof(Kind));
        }
    }

    /// <summary>사용자가 고친 칸 — 화면에서 구분해 보여 준다.</summary>
    public bool Edited { get; private set; }

    /// <summary>창체 연결 결과 등 이 칸에 붙는 설명.</summary>
    public string Note { get; set; }

    public bool HasNote => Note.Length > 0;

    /// <summary>"일반" / "창체" / "" — 색으로 구분하기 위한 값.</summary>
    public string Kind
    {
        get
        {
            if (Token.Length == 0) return "";
            return TimetableTokenNormalizer.Normalize(Token).IsCreative ? "창체" : "일반";
        }
    }

    public string Tooltip => IsPlaceholder ? "" : $"{Date:yyyy-MM-dd(ddd)} {Period}교시\n{Note}".Trim();
}

/// <summary>교시 한 줄 (월~금).</summary>
public sealed class ReviewRow
{
    public ReviewRow(int period, IReadOnlyList<ReviewCell> days)
    {
        Period = period;
        Mon = days[0]; Tue = days[1]; Wed = days[2]; Thu = days[3]; Fri = days[4];
    }

    public int Period { get; }
    public string Label => $"{Period}교시";
    public ReviewCell Mon { get; }
    public ReviewCell Tue { get; }
    public ReviewCell Wed { get; }
    public ReviewCell Thu { get; }
    public ReviewCell Fri { get; }

    public IEnumerable<ReviewCell> Cells => new[] { Mon, Tue, Wed, Thu, Fri };
}

/// <summary>주차 하나.</summary>
public sealed class ReviewWeek
{
    public ReviewWeek(DateOnly start, IReadOnlyList<ReviewRow> rows)
    {
        Start = start;
        Rows = rows;
    }

    public DateOnly Start { get; }
    public IReadOnlyList<ReviewRow> Rows { get; }

    public int LessonCount => Rows.Sum(r => r.Cells.Count(c => c.Token.Length > 0));
    public int NoteCount => Rows.Sum(r => r.Cells.Count(c => c.HasNote));

    public string Label => $"{Start:MM-dd} 주";
    public string Summary => NoteCount > 0 ? $"{LessonCount}칸 · 확인 {NoteCount}" : $"{LessonCount}칸";

    /// <summary>월~금 날짜 머리글.</summary>
    public string Mon => Head(0);
    public string Tue => Head(1);
    public string Wed => Head(2);
    public string Thu => Head(3);
    public string Fri => Head(4);

    private string Head(int dayIndex)
    {
        var cell = Rows.SelectMany(r => r.Cells).Skip(dayIndex).FirstOrDefault();
        var date = Start.AddDays(dayIndex);
        return $"{"월화수목금"[dayIndex]} {date:MM-dd}";
    }
}

/// <summary>
/// 문서 인식 결과 검토 화면 (기술설계 §6, 로드맵 T2 "인식 결과 수정 가능한 표 UI").
///
/// 사용자가 <b>눈으로 확인하고 고친 뒤에만</b> 다음 단계로 넘어간다.
/// 문서 해석은 규칙이 분명해도 학교마다 양식이 달라 완벽할 수 없다 — 마지막 판단은 사람이 한다.
/// </summary>
public sealed class TimetableReviewViewModel : ObservableObject
{
    private const int DaysPerWeek = 5;

    public TimetableReviewViewModel(
        TimetableSourcePackage package,
        IReadOnlyList<CreativeLink>? creativeLinks = null,
        IReadOnlyList<string>? pairProblems = null)
    {
        Header = $"{package.SchoolYear}학년도 {package.Semester}학기 · 원본에서 {package.Lessons.Count}칸을 읽었습니다";

        var notes = (creativeLinks ?? Array.Empty<CreativeLink>())
            .Where(l => !l.IsResolved)
            .ToDictionary(l => l.Cell, l => l.Reason);

        foreach (var week in package.Lessons.GroupBy(l => l.Cell.WeekStart).OrderBy(g => g.Key))
            Weeks.Add(BuildWeek(week.Key, week.ToList(), notes));

        Selected = Weeks.FirstOrDefault();

        // 문서 자체 경고 + 두 문서 짝 문제 + 창체 미해결 — 사용자가 봐야 할 것을 한 곳에
        foreach (var p in pairProblems ?? Array.Empty<string>()) Problems.Add("⚠ " + p);
        foreach (var w in package.Warnings.Take(30)) Problems.Add(w);

        var unresolved = notes.Count;
        if (unresolved > 0) Problems.Add($"창체 {unresolved}칸의 종류가 정해지지 않았습니다 — 표에서 확인하세요.");

        JumpToProblemCommand = new RelayCommand(JumpToProblem, () => Weeks.Any(w => w.NoteCount > 0));
    }

    public string Header { get; }
    public ObservableCollection<ReviewWeek> Weeks { get; } = new();
    public ObservableCollection<string> Problems { get; } = new();

    public bool HasProblems => Problems.Count > 0;
    public ICommand JumpToProblemCommand { get; }

    private ReviewWeek? _selected;
    public ReviewWeek? Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    /// <summary>검토·수정을 마친 결과. 빈 칸은 빠진다.</summary>
    public IReadOnlyList<TimetableSourceLesson> BuildLessons() =>
        Weeks
            .SelectMany(w => w.Rows)
            .SelectMany(r => r.Cells)
            .Where(c => !c.IsPlaceholder && c.Token.Length > 0)
            .Select(c => new TimetableSourceLesson(new TimetableCell(c.Date, c.Period), c.Token))
            .OrderBy(l => l.Cell.Date).ThenBy(l => l.Cell.Period)
            .ToList();

    public int EditedCount =>
        Weeks.SelectMany(w => w.Rows).SelectMany(r => r.Cells).Count(c => c.Edited);

    private static ReviewWeek BuildWeek(
        DateOnly monday, IReadOnlyList<TimetableSourceLesson> lessons,
        IReadOnlyDictionary<TimetableCell, string> notes)
    {
        var periods = lessons.Select(l => l.Cell.Period).DefaultIfEmpty(1).Max();
        var rows = new List<ReviewRow>(periods);

        for (var period = 1; period <= periods; period++)
        {
            var days = new List<ReviewCell>(DaysPerWeek);
            for (var d = 0; d < DaysPerWeek; d++)
            {
                var date = monday.AddDays(d);
                var found = lessons.FirstOrDefault(l => l.Cell.Date == date && l.Cell.Period == period);
                var cell = new TimetableCell(date, period);
                notes.TryGetValue(cell, out var note);

                days.Add(new ReviewCell(date, period, found?.SourceToken ?? "", note ?? ""));
            }
            rows.Add(new ReviewRow(period, days));
        }

        return new ReviewWeek(monday, rows);
    }

    private void JumpToProblem()
    {
        var target = Weeks.FirstOrDefault(w => w.NoteCount > 0);
        if (target is not null) Selected = target;
    }
}
