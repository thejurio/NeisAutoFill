using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeisAutoFill.App.Helpers;
using NeisAutoFill.App.ViewModels;


namespace NeisAutoFill.App.Views;

/// <summary>
/// 과목별 평가계획 편집표. 속은 <see cref="PlanEditorViewModel"/> 이며
/// [자료 준비] 창이 쓰던 것과 같다 — 화면만 탭 안으로 옮겼다(2026-08-22).
/// </summary>
public partial class PlanEditView : UserControl
{
    public PlanEditView() => InitializeComponent();

    private PlanEditorViewModel? Vm => DataContext as PlanEditorViewModel;

    private void PlanDeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } el)
        {
            menu.PlacementTarget = el;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void ClearAllPlans_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        if (MessageBox.Show("모든 과목의 평가계획을 지웁니다.\n(학생 명단과 이미 입력한 성적은 그대로입니다)",
                "전체 평가계획 비우기", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            Vm.ClearAllPlansCommand.Execute(null);
    }

    private void PlanGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        e.Column.CanUserSort = false;
        if (e.Column is DataGridTextColumn text)
            text.ElementStyle = (Style)PlanGrid.Resources["WrapCell"];
        e.Column.Width = e.PropertyName switch
        {
            PlanSubjectEdit.DomainColumn => new DataGridLength(110),
            PlanSubjectEdit.AchievementColumn => new DataGridLength(220),
            PlanSubjectEdit.ElementColumn => new DataGridLength(170),
            _ => new DataGridLength(1, DataGridLengthUnitType.Star),
        };
    }

    private void PlanGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control &&
            e.OriginalSource is not TextBox && Vm?.SelectedSubject is not null)
        {
            var (applied, skipped) = DataGridClipboard.Paste(PlanGrid, Vm.SelectedSubject.Grid, allowGrow: true);
            if (applied == 0 && skipped == 0)
                MessageBox.Show("클립보드에 붙여넣을 표 내용이 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
        }
    }

    private void AddPlanRow_Click(object sender, RoutedEventArgs e)
    {
        var grid = Vm?.SelectedSubject?.Grid;
        grid?.Rows.Add(grid.NewRow());
    }

    private void RemovePlanRow_Click(object sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedSubject is null) return;

        PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var rows = PlanGrid.SelectedCells
            .Select(c => c.Item).OfType<DataRowView>().Select(v => v.Row).Distinct().ToList();
        foreach (var row in rows) Vm.SelectedSubject.Grid.Rows.Remove(row);
    }
}
