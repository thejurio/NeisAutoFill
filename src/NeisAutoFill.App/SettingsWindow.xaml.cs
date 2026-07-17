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
        DialogResult = true;
    }
}
