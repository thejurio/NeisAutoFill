using System.Windows;
using System.Windows.Controls;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App.Views;

/// <summary>
/// 평가계획을 나이스에 넣는 화면. 자료는 <b>[자료 준비]에 있는 것</b>을 그대로 쓴다 —
/// 탭을 열 때마다 <see cref="EvalPlanTabViewModel.Refresh"/> 로 다시 읽는다(MainWindow 가 부른다).
/// </summary>
public partial class EvalPlanView : UserControl
{
    public EvalPlanView() => InitializeComponent();

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EvalSubjectRow row }) return;

        new EvalDetailWindow(row) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
