using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NeisAutoFill.App.Helpers;
using NeisAutoFill.App.ViewModels;

namespace NeisAutoFill.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        StateChanged += (_, _) => UpdateMaxGlyph();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 표에서 편집 중인 셀을 커밋한 뒤 저장 여부 확인
        if (!_vm.ConfirmSaveIfDirty()) e.Cancel = true;
    }

    private void UpdateMaxGlyph()
    {
        // Segoe MDL2: 최대화 E922 / 복원 E923
        BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        var path = files.FirstOrDefault(f =>
            f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase));
        if (path is not null) _vm.LoadExcel(path);
        else _vm.Log("xlsx 파일만 지원합니다.");
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogBox.ScrollToEnd();
    }

    /// <summary>[최근 ▾] 버튼 — 최근 사용한 평가계획서·성적파일 목록을 메뉴로 표시.</summary>
    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var entries = _vm.RecentEntries;

        if (entries.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "최근 파일 없음", IsEnabled = false });
        }
        else
        {
            foreach (var group in new[] { true, false })   // 평가계획서 먼저, 성적파일 다음
            {
                var items = entries.Where(x => x.IsPlan == group).ToList();
                if (items.Count == 0) continue;
                menu.Items.Add(new MenuItem
                {
                    Header = group ? "평가계획서" : "성적파일",
                    IsEnabled = false,
                    FontWeight = FontWeights.Bold,
                });
                foreach (var (path, display, _) in items)
                    menu.Items.Add(new MenuItem
                    {
                        Header = display,
                        ToolTip = path,
                        Command = _vm.OpenRecentCommand,
                        CommandParameter = path,
                    });
                menu.Items.Add(new Separator());
            }
            if (menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        }

        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>성적 표 컬럼 생성: 번호/이름은 읽기전용, 영역(등급)은 척도 드롭다운, 특기사항은 텍스트.</summary>
    private void Grid_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        var name = e.PropertyName;

        if (name is "번호" or "이름")
        {
            ((DataGridTextColumn)e.Column).IsReadOnly = true;
            e.Column.CanUserSort = false;
            return;
        }

        if (name == SubjectViewModel.NoteColumn)
        {
            e.Column.Width = 260;   // 특기사항은 넓게
            e.Column.CanUserSort = false;
            return;                  // 기본 텍스트 편집
        }

        // 영역(등급) 컬럼 → 평소엔 색 배지, 클릭하면 드롭다운 편집
        e.Column = BuildGradeColumn(name);
    }

    /// <summary>등급 컬럼: 표시=색 배지(GradeBadgeConverter), 편집=척도 드롭다운.</summary>
    private DataGridTemplateColumn BuildGradeColumn(string area)
    {
        var badgeBg = (System.Windows.Data.IValueConverter)FindResource("BadgeBg");
        var badgeFg = (System.Windows.Data.IValueConverter)FindResource("BadgeFg");
        string path = $"[{area}]";

        // 표시 템플릿: 알약 배지
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetValue(TextBlock.ForegroundProperty, new Binding(path) { Converter = badgeFg });
        text.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        text.SetValue(TextBlock.FontSizeProperty, 11.5);
        text.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new Binding(path) { Converter = badgeBg });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 3, 10, 3));
        border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.SetValue(Border.MarginProperty, new Thickness(0, 3, 0, 3));
        border.AppendChild(text);

        var cellTemplate = new DataTemplate { VisualTree = border };

        // 편집 템플릿: 척도 드롭다운 (앱 톤 콤보 스타일)
        var combo = new FrameworkElementFactory(typeof(ComboBox));
        combo.SetValue(ComboBox.ItemsSourceProperty, _vm.GradeLabels);
        combo.SetValue(ComboBox.IsEditableProperty, false);
        combo.SetValue(ComboBox.StyleProperty, (Style)FindResource("GradeEditCombo"));
        combo.SetValue(ComboBox.IsDropDownOpenProperty, true);   // 편집 진입 시 바로 펼침
        combo.SetBinding(ComboBox.SelectedItemProperty,
            new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        var editTemplate = new DataTemplate { VisualTree = combo };

        return new DataGridTemplateColumn
        {
            Header = area,
            CellTemplate = cellTemplate,
            CellEditingTemplate = editTemplate,
            ClipboardContentBinding = new Binding(path),   // 다중 셀 Ctrl+C 지원
            Width = 108,
            CanUserSort = false,
        };
    }

    // ── 성적표 다중 셀 편집 (복사/붙여넣기·일괄 지정) ──────────────

    private DataGrid? _activeGradeGrid;   // 탭 콘텐츠는 재사용되므로 그리드 인스턴스는 하나

    /// <summary>표 우클릭 메뉴: 선택 셀 일괄 등급 지정. 척도 변경 대응을 위해 열 때마다 항목 재구성.</summary>
    private void GradeGrid_Loaded(object sender, RoutedEventArgs e)
    {
        var grid = (DataGrid)sender;
        _activeGradeGrid = grid;
        if (grid.ContextMenu is not null) return;   // 탭 전환 재로드 시 중복 생성 방지
        grid.ContextMenu = new ContextMenu();
        RebuildGradeMenu(grid);
        grid.ContextMenuOpening += (_, _) => RebuildGradeMenu(grid);
    }

    // ── 일괄 입력 바 ──────────────────────────

    private void BulkAssign_Click(object sender, RoutedEventArgs e)
    {
        if (_activeGradeGrid is not null && ((Button)sender).Content is string label)
            ApplyToSelectedAreaCells(_activeGradeGrid, label);
    }

    private void BulkClear_Click(object sender, RoutedEventArgs e)
    {
        if (_activeGradeGrid is not null) ApplyToSelectedAreaCells(_activeGradeGrid, "");
    }

    private void SelectAllCells_Click(object sender, RoutedEventArgs e)
    {
        if (_activeGradeGrid is null) return;
        _activeGradeGrid.Focus();
        _activeGradeGrid.SelectAllCells();
    }

    /// <summary>이름 셀 클릭 → 그 학생의 모든 영역 선택 / 영역 헤더 클릭 → 전 학생의 그 영역 선택.
    /// Ctrl 을 누르고 클릭하면 기존 선택에 추가.</summary>
    private void GradeGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grid = (DataGrid)sender;
        if (grid.DataContext is not SubjectViewModel vm) return;
        bool additive = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // 컬럼 헤더 클릭 → 해당 영역 컬럼 전체 선택
        if (FindAncestor<System.Windows.Controls.Primitives.DataGridColumnHeader>(e.OriginalSource) is { } header)
        {
            var name = header.Column?.Header?.ToString();
            if (name is null || !vm.Areas.Contains(name)) return;   // 영역 컬럼만
            grid.Focus();
            if (!additive) grid.SelectedCells.Clear();
            foreach (var item in grid.Items)
                if (item is System.Data.DataRowView)
                    grid.SelectedCells.Add(new DataGridCellInfo(item, header.Column));
            e.Handled = true;
            return;
        }

        // 이름 셀 클릭 → 그 학생 행의 모든 영역 선택
        if (FindAncestor<DataGridCell>(e.OriginalSource) is { } cell &&
            cell.Column?.Header?.ToString() == "이름" && cell.DataContext is System.Data.DataRowView row)
        {
            grid.Focus();
            if (!additive) grid.SelectedCells.Clear();
            foreach (var col in grid.Columns)
                if (vm.Areas.Contains(col.Header?.ToString() ?? ""))
                    grid.SelectedCells.Add(new DataGridCellInfo(row, col));
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null and not T)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as T;
    }

    private void RebuildGradeMenu(DataGrid grid)
    {
        var menu = grid.ContextMenu!;
        menu.Items.Clear();
        var labels = _vm.GradeLabels.Where(l => l != "").ToList();
        for (int i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            var item = new MenuItem
            {
                Header = $"선택 셀 → {label}",
                InputGestureText = i < 9 ? $"{i + 1}" : "",
            };
            item.Click += (_, _) => ApplyToSelectedAreaCells(grid, label);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "선택 셀 지우기", InputGestureText = "Del" };
        clear.Click += (_, _) => ApplyToSelectedAreaCells(grid, "");
        menu.Items.Add(clear);
    }

    private void GradeGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var grid = (DataGrid)sender;
        // 셀 편집기(콤보·텍스트박스) 안에서 입력 중이면 개입하지 않는다
        if (e.OriginalSource is TextBox or ComboBox) return;

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            PasteGrades(grid);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            ApplyToSelectedAreaCells(grid, "");
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
            var labels = _vm.GradeLabels.Where(l => l != "").ToList();
            if (digit <= labels.Count)
            {
                ApplyToSelectedAreaCells(grid, labels[digit - 1]);
                e.Handled = true;
            }
        }
    }

    /// <summary>선택된 영역(등급) 셀에 값을 일괄 적용. 특기사항 셀은 지우기만 허용.</summary>
    private void ApplyToSelectedAreaCells(DataGrid grid, string value)
    {
        if (grid.DataContext is not SubjectViewModel vm) return;
        int applied = 0;
        foreach (var cell in grid.SelectedCells)
        {
            var header = cell.Column?.Header?.ToString();
            if (header is null || cell.Item is not DataRowView row) continue;
            bool isArea = vm.Areas.Contains(header);
            bool isNote = header == SubjectViewModel.NoteColumn;
            if (!isArea && !(isNote && value == "")) continue;   // 등급값은 영역 셀에만
            row.Row[header] = value;
            applied++;
        }
        if (applied > 0)
            _vm.Log(value == "" ? $"선택 셀 {applied}개 지움" : $"선택 셀 {applied}개 → '{value}'");
    }

    /// <summary>클립보드 표를 현재 셀 기준으로 붙여넣기. 등급 셀은 척도 라벨만 허용.</summary>
    private void PasteGrades(DataGrid grid)
    {
        if (grid.DataContext is not SubjectViewModel vm) return;
        var (applied, skipped) = DataGridClipboard.Paste(grid, vm.Grid, (column, value) =>
            column == SubjectViewModel.NoteColumn || value == "" || _vm.GradeLabels.Contains(value));
        if (applied == 0 && skipped == 0) return;
        _vm.Log($"붙여넣기: {applied}셀 적용" + (skipped > 0 ? $", {skipped}셀 건너뜀 (허용외 등급·읽기전용)" : ""));
    }
}
