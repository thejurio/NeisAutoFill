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

    private const double CriteriaPanelWidth = 300;
    private const double LogPanelHeight = 170;
    private readonly GradeGridController _gradeGrid;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        _gradeGrid = new GradeGridController(vm);
        StateChanged += (_, _) => UpdateMaxGlyph();

        // 패널 토글은 창을 바깥으로 확장 — 본문(성적표)이 구겨지지 않는다 (최대화 상태는 그대로)
        if (_vm.ShowCriteriaPanel) Width += CriteriaPanelWidth;   // 저장된 켜짐 상태 복원분
        if (_vm.LogExpanded) Height += LogPanelHeight;
        _vm.PropertyChanged += (_, e) =>
        {
            if (WindowState != WindowState.Normal) return;
            if (e.PropertyName == nameof(MainViewModel.ShowCriteriaPanel))
                Width = Math.Max(MinWidth, Width + (_vm.ShowCriteriaPanel ? CriteriaPanelWidth : -CriteriaPanelWidth));
            else if (e.PropertyName == nameof(MainViewModel.LogExpanded))
                Height = Math.Max(MinHeight, Height + (_vm.LogExpanded ? LogPanelHeight : -LogPanelHeight));
        };
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

        var excel = files.FirstOrDefault(f =>
            f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".xlsm", StringComparison.OrdinalIgnoreCase));
        if (excel is not null) { _vm.LoadExcel(excel); return; }

        // 평가계획 문서(pdf/hwp/hwpx) → 명단·계획 편집 창 열고 AI 가져오기
        var doc = files.FirstOrDefault(f =>
            f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".hwp", StringComparison.OrdinalIgnoreCase) ||
            f.EndsWith(".hwpx", StringComparison.OrdinalIgnoreCase));
        if (doc is not null) { _vm.ImportPlanDocument(doc); return; }

        _vm.Log("지원 형식: xlsx (성적·계획) / pdf·hwp·hwpx (평가계획 AI 가져오기)");
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
        var name = e.PropertyName;   // DataTable 컬럼명 (영역은 안전ID)

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

        // 영역(등급) 컬럼 → 평소엔 색 배지, 클릭하면 드롭다운 편집.
        // 바인딩은 안전 컬럼ID 로, 표시 헤더는 영역명으로 (영역명에 쉼표가 있어도 바인딩 안 깨짐).
        var vm = (sender as DataGrid)?.DataContext as SubjectViewModel;
        var header = vm?.HeaderOf(name) ?? name;
        e.Column = BuildGradeColumn(name, header);
    }

    /// <summary>등급 컬럼: 표시=색 배지(GradeBadgeConverter), 편집=척도 드롭다운.
    /// columnId=안전한 DataTable 컬럼명(바인딩용), header=화면 표시 영역명.</summary>
    private DataGridTemplateColumn BuildGradeColumn(string columnId, string header)
    {
        var badgeBg = (System.Windows.Data.IValueConverter)FindResource("BadgeBg");
        var badgeFg = (System.Windows.Data.IValueConverter)FindResource("BadgeFg");
        string path = $"[{columnId}]";   // columnId 는 특수문자 없는 안전 ID

        // 표시 템플릿: 알약 배지 (미입력은 – 로 표시)
        var dash = (IValueConverter)FindResource("EmptyDash");
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path) { Converter = dash });
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
            Header = header,
            CellTemplate = cellTemplate,
            CellEditingTemplate = editTemplate,
            ClipboardContentBinding = new Binding(path),   // 다중 셀 Ctrl+C 지원
            Width = 108,
            CanUserSort = false,
        };
    }

    // ── 성적표 다중 셀 상호작용 → Helpers/GradeGridController 위임 (R4) ──

    private void GradeGrid_Loaded(object sender, RoutedEventArgs e) =>
        _gradeGrid.OnLoaded((DataGrid)sender);

    private void GradeGrid_PreviewKeyDown(object sender, KeyEventArgs e) =>
        _gradeGrid.OnPreviewKeyDown((DataGrid)sender, e);

    private void GradeGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        _gradeGrid.OnPreviewMouseLeftButtonDown((DataGrid)sender, e);

    private void BulkAssign_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Content is string label) _gradeGrid.BulkAssign(label);
    }

    private void BulkClear_Click(object sender, RoutedEventArgs e) => _gradeGrid.BulkClear();

    private void SelectAllCells_Click(object sender, RoutedEventArgs e) => _gradeGrid.SelectAll();
}
