using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>과목 선택 대화상자 (전과목 생성·입력 공용). DialogResult=true 면 호출 측이 IsChecked 를 읽는다.</summary>
public partial class BatchGenerateWindow : Window
{
    private readonly IReadOnlyList<SubjectPick> _picks;

    public BatchGenerateWindow(IReadOnlyList<SubjectPick> picks,
        string? title = null, string? description = null, string? startLabel = null,
        string? warning = null)
    {
        InitializeComponent();
        DataContext = _picks = picks;
        if (title is not null) { TitleText.Text = title; Title = title; }
        if (description is not null) DescText.Text = description;
        if (startLabel is not null) StartButton.Content = startLabel;
        if (warning is not null)
        {
            DescText.Text += "\n\n★ " + warning;
            DescText.Foreground = System.Windows.Media.Brushes.Firebrick;
        }
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
