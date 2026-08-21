using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>
/// 평가계획을 나이스에 넣는 창 — 평가 탭의 [자료 준비]·[교과학습] 과 같은 자리다.
/// 새 탭을 만들지 않고 <b>평가 탭 안</b>에 둔다 (사용자 결정 2026-08-21).
/// </summary>
public partial class EvalPlanWindow : Window
{
    public EvalPlanWindow(EvalPlanTabViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // [자료 준비]에서 읽은 문서가 있으면 창이 뜨자마자 그것을 쓴다 — 두 번 고르지 않게
        Loaded += async (_, _) => await vm.LoadOfferedAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
