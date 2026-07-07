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
}
