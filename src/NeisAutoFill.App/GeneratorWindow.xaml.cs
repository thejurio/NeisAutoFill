using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

public partial class GeneratorWindow : Window
{
    public GeneratorWindow(GeneratorViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>드롭다운 버튼 — 버튼 아래에 삭제 메뉴를 연다.</summary>
    private void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } el)
        {
            menu.PlacementTarget = el;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
