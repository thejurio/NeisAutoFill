using System.Windows;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

public partial class ScaleEditorWindow : Window
{
    private readonly ScaleEditorViewModel _vm;

    public ScaleEditorWindow(ScaleEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var error = _vm.TrySave();
        if (error is not null)
        {
            MessageBox.Show(error, "저장 불가", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
