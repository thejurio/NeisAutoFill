using System.Windows;
using System.Windows.Controls;

namespace NeisAutoFill.App.Views;

public partial class GeneratorView : UserControl
{
    public GeneratorView() => InitializeComponent();

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
