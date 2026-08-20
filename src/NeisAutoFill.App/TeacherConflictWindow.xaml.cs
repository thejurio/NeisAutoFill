using System.Windows;

namespace NeisAutoFill.App;

/// <summary>과목 기본 교사를 바꿀 때, 예외가 그 선택을 가리고 있으면 무엇을 할지.</summary>
public enum TeacherConflictChoice
{
    /// <summary>예외를 지우고 고른 교사로 전부 바꾼다.</summary>
    Replace,
    /// <summary>예외는 그대로 두고 나머지 시간만 바꾼다.</summary>
    Keep,
    Cancel,
}

/// <summary>
/// 과목 기본 교사를 바꿨는데 <b>예외가 다른 교사를 가리키고 있을 때</b> 묻는다.
///
/// 예외가 없거나, 예외가 이미 같은 교사를 가리키면 <b>묻지 않는다</b> —
/// 결과가 달라지지 않는 일로 사람을 멈춰 세우면 안 된다.
/// </summary>
public partial class TeacherConflictWindow : Window
{
    public TeacherConflictWindow(
        string subject, string newTeacher, string exceptionTeacher,
        int coveredHours, int regularCount, int onceCount, bool fullyCovered)
    {
        InitializeComponent();

        TitleText.Text = $"{subject} — 예외가 있는 과목";
        HeadText.Text = $"{subject} 담당 교사를 {newTeacher} (으)로 바꿉니다.";

        var kinds = new List<string>();
        if (regularCount > 0) kinds.Add($"매주 {regularCount}건");
        if (onceCount > 0) kinds.Add($"하루 {onceCount}건");

        BodyText.Text =
            $"이 과목에는 예외가 {coveredHours}시간 있습니다 ({string.Join(" · ", kinds)}).\n" +
            $"그 시간은 {exceptionTeacher} (으)로 들어갑니다.\n\n" +
            (fullyCovered
                ? "모든 시간이 예외로 덮여 있어, 예외를 두면 화면이 바뀌지 않습니다."
                : "예외를 두면 나머지 시간만 바뀝니다.");

        ReplaceButton.Content = $"예외 지우고 전부 {newTeacher}";
        KeepButton.Content = "예외 두고 나머지만";
    }

    public TeacherConflictChoice Choice { get; private set; } = TeacherConflictChoice.Cancel;

    public static TeacherConflictChoice Ask(
        string subject, string newTeacher, string exceptionTeacher,
        int coveredHours, int regularCount, int onceCount, bool fullyCovered, Window? owner)
    {
        var w = new TeacherConflictWindow(
            subject, newTeacher, exceptionTeacher, coveredHours, regularCount, onceCount, fullyCovered)
        { Owner = owner };

        w.ShowDialog();
        return w.Choice;
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        Choice = TeacherConflictChoice.Replace;
        DialogResult = true;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Choice = TeacherConflictChoice.Keep;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = TeacherConflictChoice.Cancel;
        DialogResult = false;
    }
}
