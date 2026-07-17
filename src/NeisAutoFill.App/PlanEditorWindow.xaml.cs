using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeisAutoFill.App.Helpers;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

/// <summary>명단·평가계획 편집 창. 저장 시 DialogResult=true — 호출 측이 파일 저장·재로드.</summary>
public partial class PlanEditorWindow : Window
{
    private readonly PlanEditorViewModel _vm;

    public PlanEditorWindow(PlanEditorViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitGridEdits();
        if (_vm.Build(out var error) is null)
        {
            MessageBox.Show(error, "확인 필요", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void CommitGridEdits()
    {
        RosterGrid.CommitEdit(DataGridEditingUnit.Row, true);
        PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    // ── 초기화 ────────────────────────────

    private void ClearRoster_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("학생 명단을 모두 지웁니다.\n(평가계획과 이미 입력한 성적은 그대로입니다)",
                "명단 비우기", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _vm.ClearRosterCommand.Execute(null);
    }

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
        if (MessageBox.Show("모든 과목의 평가계획을 지웁니다.\n(학생 명단과 이미 입력한 성적은 그대로입니다)",
                "전체 평가계획 비우기", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            _vm.ClearAllPlansCommand.Execute(null);
    }

    // ── 명단 ──────────────────────────────

    private void PasteRoster_Click(object sender, RoutedEventArgs e)
    {
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        var count = _vm.PasteRoster(text);
        if (count == 0)
            MessageBox.Show("클립보드에 명단으로 읽을 내용이 없습니다.\n엑셀에서 번호·이름 열(또는 이름 열만)을 복사한 뒤 다시 시도하세요.",
                "안내", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RosterGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && e.OriginalSource is not TextBox)
        {
            PasteRoster_Click(sender, e);
            e.Handled = true;
        }
    }

    // ── 평가계획 표 ────────────────────────

    private void PlanGrid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        e.Column.CanUserSort = false;
        if (e.Column is DataGridTextColumn text)
            text.ElementStyle = (Style)PlanGrid.Resources["WrapCell"];
        e.Column.Width = e.PropertyName switch
        {
            PlanSubjectEdit.DomainColumn => new DataGridLength(110),
            PlanSubjectEdit.AchievementColumn => new DataGridLength(220),
            _ => new DataGridLength(1, DataGridLengthUnitType.Star),
        };
    }

    private void PlanGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control &&
            e.OriginalSource is not TextBox && _vm.SelectedSubject is not null)
        {
            var (applied, skipped) = DataGridClipboard.Paste(PlanGrid, _vm.SelectedSubject.Grid, allowGrow: true);
            if (applied == 0 && skipped == 0)
                MessageBox.Show("클립보드에 붙여넣을 표 내용이 없습니다.", "안내",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            e.Handled = true;
        }
    }

    private void AddPlanRow_Click(object sender, RoutedEventArgs e)
    {
        var grid = _vm.SelectedSubject?.Grid;
        grid?.Rows.Add(grid.NewRow());
    }

    private void RemovePlanRow_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedSubject is null) return;
        PlanGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var rows = PlanGrid.SelectedCells
            .Select(c => c.Item).OfType<DataRowView>().Select(v => v.Row).Distinct().ToList();
        foreach (var row in rows) _vm.SelectedSubject.Grid.Rows.Remove(row);
    }
}
