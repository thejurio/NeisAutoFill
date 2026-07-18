using System.Windows;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;

namespace NeisAutoFill.App;

/// <summary>전과목 입력 결과 대시보드 창.</summary>
public partial class BatchResultWindow : Window
{
    public BatchResultWindow(BatchResultViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>결과 대시보드를 모달로 띄운다. retry = 실패·미도달 과목 재실행 함수.</summary>
    public static void ShowResult(
        IReadOnlyList<BatchUploadRunner.SubjectOutcome> outcomes, string unit,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<BatchUploadRunner.SubjectOutcome>>> retry,
        Window? owner)
    {
        var win = new BatchResultWindow(new BatchResultViewModel(outcomes, unit, retry)) { Owner = owner };
        win.ShowDialog();
    }
}
