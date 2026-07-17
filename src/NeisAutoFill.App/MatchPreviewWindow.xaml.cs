using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>매칭 미리보기 대화상자. DialogResult=true 면 호출 측이 BuildDecision() 을 읽는다.</summary>
public partial class MatchPreviewWindow : Window
{
    private readonly MatchPreviewViewModel _vm;

    public MatchPreviewWindow(MatchPreviewViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Validate() is { } problem)
        {
            MessageBox.Show(problem, "확인 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
