using System.Windows;
using NeisAutoFill.App.Services;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>통합 설정 창. 저장 성공 시 DialogResult=true — 호출 측이 화면 갱신.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "작업공간 백업 저장",
            FileName = WorkspaceBackup.SuggestFileName(DateTime.Now),
            Filter = "백업 파일 (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;

        var (ok, error, count) = WorkspaceBackup.Create(dlg.FileName);
        if (ok)
            MessageBox.Show($"{count}개 파일을 백업했습니다.\n{dlg.FileName}",
                "백업 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show($"백업하지 못했습니다: {error}",
                "백업 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "복원할 백업 파일 선택",
            Filter = "백업 파일 (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dlg.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(
            "선택한 백업으로 되돌립니다.\n" +
            "현재의 명단·평가계획·성적·서술문·설정이 백업 내용으로 덮어써집니다.\n" +
            "이 작업은 되돌릴 수 없습니다.\n\n" +
            "복원 후 프로그램이 자동으로 재시작됩니다. 계속할까요?",
            "백업에서 복원", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        var (ok, error, count) = WorkspaceBackup.Restore(dlg.FileName);
        if (!ok)
        {
            MessageBox.Show($"복원하지 못했습니다: {error}", "복원 실패",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        MessageBox.Show($"{count}개 파일을 복원했습니다.\n프로그램을 재시작합니다.",
            "복원 완료", MessageBoxButton.OK, MessageBoxImage.Information);
        AppReset.RestartApp();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "설정·명단·성적·서술문을 모두 지우고 처음 실행한 상태로 되돌립니다.\n" +
            "이 작업은 되돌릴 수 없습니다.\n\n" +
            "정말 초기화할까요?",
            "처음 상태로 초기화", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        // 작업공간 엑셀(성적·계획·서술문)까지 지울지 한 번 더 확인
        var workspace = MessageBox.Show(
            "내 문서\\NeisAutoFill 폴더의 엑셀 파일(성적·평가계획서·서술문)도 함께 지울까요?\n\n" +
            "[예] 완전히 삭제 (파일까지)\n" +
            "[아니오] 프로그램 안 자료만 초기화 (엑셀 파일은 남김)",
            "엑셀 파일 삭제 여부", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (workspace == MessageBoxResult.Cancel) return;

        AppReset.ResetAndRestart(alsoWorkspaceFiles: workspace == MessageBoxResult.Yes);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.TrySave() is { } error)
        {
            MessageBox.Show(error, "확인 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 학급 모드(담임/전담) 저장 — 바뀌면 자료 경로가 달라지므로 재시작
        if (_vm.SaveModeReturnsNeedsRestart())
        {
            MessageBox.Show(
                "학급 모드가 바뀌어 프로그램을 재시작합니다.\n담임과 전담의 자료는 서로 따로 보관됩니다.",
                "모드 전환", MessageBoxButton.OK, MessageBoxImage.Information);
            AppReset.RestartApp();
            return;
        }
        DialogResult = true;
    }
}
