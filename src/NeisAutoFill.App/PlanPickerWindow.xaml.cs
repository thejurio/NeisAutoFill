using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>평가계획 인식 후 불러올 과목(담임)/학년·과목(전담)을 고르는 대화상자 (F9 M4b).</summary>
public partial class PlanPickerWindow : Window
{
    private readonly PlanPickerViewModel _vm;

    public PlanPickerWindow(PlanPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.AnyChecked)
        {
            MessageBox.Show("하나 이상 선택하세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        var on = SelectAll.IsChecked == true;
        foreach (var item in _vm.Items) item.IsChecked = on;
    }
}
