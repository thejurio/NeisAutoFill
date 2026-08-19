using System.Windows;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App;

/// <summary>
/// 입력할 기간을 고르는 창 (로드맵 T8).
///
/// 연간 전체를 한 번에 넣을 필요는 없다 — 학기 초에 한 달치만, 나중에 나머지처럼 나눠 쓸 수 있어야 한다.
/// 덮어쓰기 선택도 여기서 함께 받는다: 나이스 학급시간표는 기준시간표에서 이미 채워져 있는 경우가 흔해
/// 덮어쓰기를 끄면 대부분의 칸에서 멈추게 된다.
/// </summary>
public partial class TimetableRangeWindow : Window
{
    private readonly IReadOnlyList<TimetableSourceLesson> _lessons;
    private readonly DateOnly _min;
    private readonly DateOnly _max;

    public TimetableRangeWindow(IReadOnlyList<TimetableSourceLesson> lessons)
    {
        InitializeComponent();

        _lessons = lessons;
        _min = lessons.Min(l => l.Cell.Date);
        _max = lessons.Max(l => l.Cell.Date);

        SourceRangeText.Text = $"문서에서 읽은 기간: {_min:yyyy-MM-dd} ~ {_max:yyyy-MM-dd} · 수업 {lessons.Count}칸";

        FromPicker.DisplayDateStart = ToPicker.DisplayDateStart = ToDateTime(_min);
        FromPicker.DisplayDateEnd = ToPicker.DisplayDateEnd = ToDateTime(_max);
        FromPicker.SelectedDate = ToDateTime(_min);
        ToPicker.SelectedDate = ToDateTime(_max);

        Refresh();
    }

    /// <summary>고른 범위. 취소하면 null.</summary>
    public TimetableRangeChoice? Choice { get; private set; }

    public static TimetableRangeChoice? Ask(IReadOnlyList<TimetableSourceLesson> lessons, Window? owner)
    {
        var w = new TimetableRangeWindow(lessons) { Owner = owner };
        return w.ShowDialog() == true ? w.Choice : null;
    }

    private static DateTime ToDateTime(DateOnly d) => d.ToDateTime(TimeOnly.MinValue);

    private DateOnly From => DateOnly.FromDateTime(FromPicker.SelectedDate ?? ToDateTime(_min));
    private DateOnly To => DateOnly.FromDateTime(ToPicker.SelectedDate ?? ToDateTime(_max));

    private void Refresh()
    {
        // 창이 다 만들어지기 전에도 SelectedDateChanged 가 오므로 방어한다
        if (CountText is null) return;

        var count = From > To ? 0 : _lessons.Count(l => l.Cell.Date >= From && l.Cell.Date <= To);

        CountText.Text = From > To
            ? "시작일이 종료일보다 뒤입니다."
            : $"선택한 기간의 수업 {count}칸";

        OkButton.IsEnabled = count > 0;
    }

    private void Range_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => Refresh();

    private void Whole_Click(object sender, RoutedEventArgs e)
    {
        FromPicker.SelectedDate = ToDateTime(_min);
        ToPicker.SelectedDate = ToDateTime(_max);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Choice = new TimetableRangeChoice(From, To, OverwriteCheck.IsChecked == true);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
