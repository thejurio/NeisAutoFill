using System.Windows;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;

namespace NeisAutoFill.App;

/// <summary>
/// 연간 입력이 끝난(또는 멈춘) 뒤의 결과 대시보드 (로드맵 T8).
/// 멈췄으면 [이어서 다시 시도]로 그 주부터 다시 갈 수 있다.
/// </summary>
public partial class TimetableResultWindow : Window
{
    public TimetableResultWindow(TimetableResultViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    /// <summary>결과를 보여 주고, 사용자가 다시 시도를 고르면 true.</summary>
    public static bool AskRetry(BatchRunResult result, Window? owner)
    {
        var w = new TimetableResultWindow(new TimetableResultViewModel(result)) { Owner = owner };
        return w.ShowDialog() == true;
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
