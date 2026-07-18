using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// 성적표 DataGrid 상호작용 전담 (일괄 지정·복붙·Ctrl+Z·행/열 선택·우클릭 메뉴).
/// MainWindow 는 이벤트를 이 컨트롤러로 위임만 한다 — UI 로직을 창 코드비하인드에서 분리 (R4).
/// </summary>
public sealed class GradeGridController(MainViewModel main)
{
    private DataGrid? _active;   // 탭 콘텐츠는 재사용되므로 그리드 인스턴스는 하나

    public void OnLoaded(DataGrid grid)
    {
        _active = grid;
        if (grid.ContextMenu is not null) return;   // 탭 전환 재로드 시 중복 생성 방지
        grid.ContextMenu = new ContextMenu();
        RebuildMenu(grid);
        grid.ContextMenuOpening += (_, _) => RebuildMenu(grid);   // 척도 변경 대응 — 열 때마다 재구성
    }

    // ── 일괄 입력 바 (버튼) ─────────────────────

    public void BulkAssign(string label) { if (_active is not null) ApplyToSelected(_active, label); }
    public void BulkClear() { if (_active is not null) ApplyToSelected(_active, ""); }

    public void SelectAll()
    {
        if (_active is null) return;
        _active.Focus();
        _active.SelectAllCells();
    }

    // ── 키보드 ─────────────────────────────

    public void OnPreviewKeyDown(DataGrid grid, KeyEventArgs e)
    {
        // 셀 편집기(콤보·텍스트박스) 안에서 입력 중이면 개입하지 않는다
        if (e.OriginalSource is TextBox or ComboBox) return;

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Paste(grid);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (grid.DataContext is SubjectViewModel vz && vz.Undo()) main.Log("↩ 실행 취소");
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            ApplyToSelected(grid, "");
            e.Handled = true;
            return;
        }

        // 숫자 1~9 = 척도 단계 일괄 지정 (여러 셀 선택 후)
        int digit = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
            _ => 0,
        };
        if (digit > 0 && Keyboard.Modifiers == ModifierKeys.None && grid.SelectedCells.Count > 1)
        {
            var labels = main.GradeLabels.Where(l => l != "").ToList();
            if (digit <= labels.Count)
            {
                ApplyToSelected(grid, labels[digit - 1]);
                e.Handled = true;
            }
        }
    }

    // ── 마우스: 이름 셀 = 학생 전체 / 영역 헤더 = 영역 전체 (Ctrl = 선택 추가) ──

    public void OnPreviewMouseLeftButtonDown(DataGrid grid, MouseButtonEventArgs e)
    {
        if (grid.DataContext is not SubjectViewModel vm) return;
        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource) is { } header)
        {
            var name = header.Column?.Header?.ToString();
            if (name is null || !vm.Areas.Contains(name)) return;   // 영역 컬럼만
            grid.Focus();
            if (!additive) grid.SelectedCells.Clear();
            foreach (var item in grid.Items)
                if (item is DataRowView)
                    grid.SelectedCells.Add(new DataGridCellInfo(item, header.Column));
            e.Handled = true;
            return;
        }

        if (FindAncestor<DataGridCell>(e.OriginalSource) is { } cell &&
            cell.Column?.Header?.ToString() == "이름" && cell.DataContext is DataRowView row)
        {
            grid.Focus();
            if (!additive) grid.SelectedCells.Clear();
            foreach (var col in grid.Columns)
                if (vm.Areas.Contains(col.Header?.ToString() ?? ""))
                    grid.SelectedCells.Add(new DataGridCellInfo(row, col));
            e.Handled = true;
        }
    }

    // ── 내부 ─────────────────────────────

    private void RebuildMenu(DataGrid grid)
    {
        var menu = grid.ContextMenu!;
        menu.Items.Clear();
        var labels = main.GradeLabels.Where(l => l != "").ToList();
        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var item = new MenuItem
            {
                Header = $"선택 셀 → {label}",
                InputGestureText = i < 9 ? $"{i + 1}" : "",
            };
            item.Click += (_, _) => ApplyToSelected(grid, label);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "선택 셀 지우기", InputGestureText = "Del" };
        clear.Click += (_, _) => ApplyToSelected(grid, "");
        menu.Items.Add(clear);
    }

    /// <summary>선택된 영역(등급) 셀에 값 일괄 적용. 특기사항은 지우기만 허용. Ctrl+Z 한 번에 되돌려짐.</summary>
    private void ApplyToSelected(DataGrid grid, string value)
    {
        if (grid.DataContext is not SubjectViewModel vm) return;
        int applied = 0;
        vm.BeginBulkEdit();
        try
        {
            foreach (var cell in grid.SelectedCells)
            {
                var header = cell.Column?.Header?.ToString();
                if (header is null || cell.Item is not DataRowView row) continue;
                bool isArea = vm.Areas.Contains(header);
                bool isNote = header == SubjectViewModel.NoteColumn;
                if (!isArea && !(isNote && value == "")) continue;   // 등급값은 영역 셀에만
                row.Row[vm.DataColumnOf(header)] = value;            // 영역은 안전ID 로 접근
                applied++;
            }
        }
        finally { vm.EndBulkEdit(); }
        if (applied > 0)
            main.Log(value == "" ? $"선택 셀 {applied}개 지움" : $"선택 셀 {applied}개 → '{value}' (Ctrl+Z 로 취소 가능)");
    }

    /// <summary>클립보드 표 붙여넣기. 등급 셀은 척도 라벨만 허용. Ctrl+Z 한 번에 되돌려짐.</summary>
    private void Paste(DataGrid grid)
    {
        if (grid.DataContext is not SubjectViewModel vm) return;
        vm.BeginBulkEdit();
        int applied, skipped;
        try
        {
            (applied, skipped) = DataGridClipboard.Paste(grid, vm.Grid,
                validate: (header, value) =>
                    header == SubjectViewModel.NoteColumn || value == "" || main.GradeLabels.Contains(value),
                resolveColumn: vm.DataColumnOf);   // 영역 헤더 → 안전 컬럼ID
        }
        finally { vm.EndBulkEdit(); }
        if (applied == 0 && skipped == 0) return;
        main.Log($"붙여넣기: {applied}셀 적용" + (skipped > 0 ? $", {skipped}셀 건너뜀 (허용외 등급·읽기전용)" : ""));
    }

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null and not T)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
