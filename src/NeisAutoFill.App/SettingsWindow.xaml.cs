using System.Windows;
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
