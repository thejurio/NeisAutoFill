using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>
/// 평가계획을 나이스에 넣는 창 — 평가 탭의 [자료 준비]·[교과학습] 과 같은 자리다.
/// 자료는 <b>[자료 준비]에 있는 것</b>을 그대로 쓴다 — 창이 열릴 때마다 다시 읽는다.
/// </summary>
public partial class EvalPlanWindow : Window
{
    private readonly EvalPlanTabViewModel _vm;

    public EvalPlanWindow(EvalPlanTabViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        // 자료 준비에서 고친 것이 바로 보이도록 열 때마다 새로 읽는다
        Loaded += (_, _) => _vm.Refresh();
    }

    private void Detail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: EvalSubjectRow row }) return;

        new EvalDetailWindow(row) { Owner = this }.ShowDialog();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
