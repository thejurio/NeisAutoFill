using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>자료 준비 팝업 — MainViewModel 의 파일 커맨드·상태를 그대로 바인딩.</summary>
public partial class DataPrepWindow : Window
{
    public DataPrepWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
