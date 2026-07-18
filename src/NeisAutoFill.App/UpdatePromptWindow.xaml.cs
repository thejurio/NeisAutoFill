using System.Windows;

namespace NeisAutoFill.App;

/// <summary>업데이트 안내 창 — 릴리스 노트를 보여주고 [지금 업데이트]/[나중에] 를 묻는다.
/// '나중에'는 영구 건너뛰기가 아니라 다음 실행 때 다시 안내된다.</summary>
public partial class UpdatePromptWindow : Window
{
    private UpdatePromptWindow() => InitializeComponent();

    /// <summary>true = 지금 업데이트, false = 나중에.</summary>
    public static bool Ask(string latest, string current, string notes, Window? owner)
    {
        var win = new UpdatePromptWindow { Owner = owner };
        win.HeadText.Text = $"새 버전 v{latest} 이 있습니다  (현재 v{current})";
        win.NotesBox.Text = string.IsNullOrWhiteSpace(notes)
            ? "이번 버전의 변경 내용 안내가 없습니다."
            : notes.Trim();
        return win.ShowDialog() == true;
    }

    private void Now_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Later_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
