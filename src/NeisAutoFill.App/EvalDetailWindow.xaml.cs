using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>
/// 교과 하나의 평가계획을 펼쳐 보여 준다 — 영역 · 성취기준 · 단계별 평가기준.
/// 넣기 전에 <b>무엇이 들어가는지</b> 눈으로 확인하는 자리다.
/// </summary>
public partial class EvalDetailWindow : Window
{
    public EvalDetailWindow(EvalSubjectRow row)
    {
        InitializeComponent();
        TitleText.Text = $"{row.Subject} — {row.Summary}";
        AreaList.ItemsSource = row.Details;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
