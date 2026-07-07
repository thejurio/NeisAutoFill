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
}
