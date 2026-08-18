using System.Windows;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App;

/// <summary>
/// 시간표 과목·교사 연결 창 (기술설계 §11).
/// 모든 표기가 정해질 때까지 [적용]이 비활성 — 미해결을 남긴 채 입력하지 않는다.
/// </summary>
public partial class TimetableMappingWindow : Window
{
    private readonly TimetableMappingViewModel _vm;

    public TimetableMappingWindow(TimetableMappingViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    /// <summary>확정된 규칙. 취소하면 null.</summary>
    public IReadOnlyList<TimetableMappingRule>? Rules { get; private set; }

    /// <summary>창을 띄워 규칙을 받는다. 취소 시 null.</summary>
    public static IReadOnlyList<TimetableMappingRule>? Ask(TimetableMappingViewModel vm, Window? owner)
    {
        var w = new TimetableMappingWindow(vm) { Owner = owner };
        return w.ShowDialog() == true ? w.Rules : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Rules = _vm.BuildRules();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
