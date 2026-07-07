using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            Width = 108,
            CanUserSort = false,
        };
    }
}
