using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>전과목 생성 과목 선택 대화상자. DialogResult=true 면 호출 측이 IsChecked 를 읽는다.</summary>
public partial class BatchGenerateWindow : Window
{
    private readonly IReadOnlyList<SubjectPick> _picks;

    public BatchGenerateWindow(IReadOnlyList<SubjectPick> picks)
    {
        InitializeComponent();
        DataContext = _picks = picks;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!_picks.Any(p => p.IsChecked))
        {
            MessageBox.Show("선택된 과목이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void All_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _picks) if (p.EligibleCount > 0) p.IsChecked = true;
    }

    private void None_Click(object sender, RoutedEventArgs e)
    {
        foreach (var p in _picks) p.IsChecked = false;
    }
}
