using System.Windows;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App;

/// <summary>
/// 실제 입력·저장 직전의 마지막 동의 창 (로드맵 T8 "자동 저장 명시적 동의").
///
/// 여기까지는 나이스를 읽기만 했다. <b>이 창의 [입력 시작] 이 되돌릴 수 없는 첫 행동이다</b> —
/// 그래서 무엇이 일어나는지 숫자로 보여 주고, 체크를 해야만 버튼이 열린다.
/// </summary>
public partial class TimetableRunConsentWindow : Window
{
    public TimetableRunConsentWindow(string summary, string? resumeNote)
    {
        InitializeComponent();
        SummaryText.Text = summary;

        if (!string.IsNullOrWhiteSpace(resumeNote))
        {
            ResumeText.Text = resumeNote;
            ResumeBox.Visibility = Visibility.Visible;
        }
    }

    /// <summary>동의를 받으면 true. 취소하면 false.</summary>
    /// <param name="range">사용자가 고른 기간과 덮어쓰기 여부</param>
    public static bool Ask(
        TimetablePlan plan, TimetableRunCheckpoint checkpoint, string? resumeBlocker,
        TimetableRangeChoice range, Window? owner)
    {
        var weeks = plan.ByWeek.Select(w => w.Key).ToList();
        var todo = weeks.Count(w => !checkpoint.IsCompleted(w));

        var summary =
            $"기간 {range.From:yyyy-MM-dd} ~ {range.To:yyyy-MM-dd}\n" +
            $"입력할 칸 {plan.Writable.Count}칸 · 주 {todo}개\n" +
            $"이미 같아서 건드리지 않는 칸 {plan.CountByStatus.GetValueOrDefault(AssignmentStatus.AlreadyMatches)}칸\n" +
            (range.AllowOverwrite
                ? "\n이미 다른 값이 들어 있는 칸은 계획대로 덮어씁니다.\n"
                : "\n이미 다른 값이 들어 있는 칸은 건드리지 않고 거기서 멈춥니다.\n") +
            "\n주 하나마다 이 순서로 진행합니다:\n" +
            "그 주로 이동 → 지금 값 다시 확인 → 입력 → 저장 → 다시 조회해서 검증";

        var note = resumeBlocker is not null
            ? $"이전 기록이 있지만 이어서 쓰지 않습니다 — {resumeBlocker} 처음부터 다시 확인합니다."
            : checkpoint.CompletedWeeks.Count > 0
                ? $"이어서 진행합니다 — {checkpoint.Describe()}. 끝난 주는 다시 건드리지 않습니다."
                : null;

        var w = new TimetableRunConsentWindow(summary, note) { Owner = owner };
        return w.ShowDialog() == true;
    }

    private void Consent_Changed(object sender, RoutedEventArgs e) =>
        StartButton.IsEnabled = ConsentCheck.IsChecked == true;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
