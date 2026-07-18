using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>학년·반 추가 대화상자 (F9 M4a).</summary>
public partial class AddClassDialog : Window
{
    private readonly AddClassDialogViewModel _vm;

    public AddClassDialog(AddClassDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsValid)
        {
            MessageBox.Show("반 이름을 확인하세요 (예: 1). \\ / : * ? \" < > | 는 쓸 수 없습니다.",
                "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
