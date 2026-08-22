using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            // 명단도 여기서 고친다 — 고치면 <b>모든 과목에 같이</b> 반영된다(SubjectViewModel).
            e.Column.CanUserSort = false;
            e.Column.Width = name == "번호" ? 54 : 92;
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

    private void AddStudent_Click(object sender, RoutedEventArgs e) => _gradeGrid.AddStudent();


    private void BulkAssign_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Content is string label) _gradeGrid.BulkAssign(label);
    }

    private void BulkClear_Click(object sender, RoutedEventArgs e) => _gradeGrid.BulkClear();

    private void SelectAllCells_Click(object sender, RoutedEventArgs e) => _gradeGrid.SelectAll();

    /// <summary>❓ 버튼을 Ctrl+Shift+클릭하면 진단 버튼을 토글 (숨김↔표시). 그냥 클릭은 도움말 열기.</summary>
    private void Help_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        const ModifierKeys combo = ModifierKeys.Control | ModifierKeys.Shift;
        if ((Keyboard.Modifiers & combo) == combo)
        {
            _vm.ToggleDiagButton();
            e.Handled = true;   // 도움말은 열지 않음
        }
    }

    // ── 큰 축 탭의 미끄러지는 조각 ────────────────────────────────────────
    //
    // 고른 칸을 파랗게 <b>칠하는</b> 대신, 조각 하나가 <b>옮겨 간다.</b>
    // 켜고 끄는 것이 아니라 <b>둘 중 하나로 바뀌는 것</b>임을 움직임으로 보여 준다.
    //
    // 자리와 너비를 코드가 정하는 이유: 라벨 길이가 달라 칸 너비가 제각각이라
    // (평가 2자 · 시간표 3자) XAML 로는 못 박을 수 없다. 못 박았더니 짧은 쪽만 헐렁해 보였다.

    private static readonly Duration SlideTime = new(TimeSpan.FromMilliseconds(220));

    private void MainTabs_Loaded(object sender, RoutedEventArgs e)
    {
        MoveTabThumb(animate: false);
        PaintTabLabels(animate: false);
    }

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;   // 안쪽 탭의 이벤트는 무시
        MoveTabThumb(animate: true);
        PaintTabLabels(animate: true);
    }

    /// <summary>조각을 고른 칸 위로 옮긴다. 처음 그릴 때는 움직이지 않고 그 자리에 둔다.</summary>
    private void MoveTabThumb(bool animate)
    {
        if (MainTabs.Template is null) return;
        if (MainTabs.Template.FindName("Thumb", MainTabs) is not Border thumb) return;
        if (MainTabs.Template.FindName("Segments", MainTabs) is not Panel segments) return;
        if (MainTabs.ItemContainerGenerator.ContainerFromIndex(MainTabs.SelectedIndex) is not TabItem tab) return;
        if (thumb.RenderTransform is not TranslateTransform shift) return;

        // 아직 자리를 못 잡았으면(첫 그리기) 다 그려진 뒤에 다시 부른다
        if (tab.ActualWidth < 1)
        {
            Dispatcher.BeginInvoke(() => MoveTabThumb(animate), System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }

        var x = tab.TranslatePoint(new Point(0, 0), segments).X;

        if (!animate)
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            thumb.BeginAnimation(WidthProperty, null);
            shift.X = x;
            thumb.Width = tab.ActualWidth;
            thumb.Height = tab.ActualHeight;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        thumb.Height = tab.ActualHeight;
        shift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(x, SlideTime) { EasingFunction = ease });
        thumb.BeginAnimation(WidthProperty,
            new DoubleAnimation(tab.ActualWidth, SlideTime) { EasingFunction = ease });
    }

    /// <summary>고른 칸은 흰 글자, 나머지는 회색. 미끄러지는 동안에는 <b>색도 같이 건너간다.</b></summary>
    /// <remarks>
    /// 색을 즉시 바꾸면, 조각이 아직 오는 중인데 글자만 먼저 하얘져
    /// <b>회색 위의 흰 글씨</b>가 되어 잠깐 안 보인다. 조각과 같은 시간·같은 가감속으로 물들인다.
    /// </remarks>
    private void PaintTabLabels(bool animate)
    {
        for (var i = 0; i < MainTabs.Items.Count; i++)
        {
            if (MainTabs.ItemContainerGenerator.ContainerFromIndex(i) is not TabItem tab) continue;

            var want = i == MainTabs.SelectedIndex ? Colors.White : UnselectedTabColor;

            // 칸마다 제 브러시를 쥐고 있어야 각자 물들일 수 있다 (템플릿의 것은 공유·동결이다)
            if (tab.Foreground is not SolidColorBrush brush || brush.IsFrozen)
                tab.Foreground = brush = new SolidColorBrush(want);

            if (!animate)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = want;
                continue;
            }

            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(want, SlideTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
        }
    }

    /// <summary>고르지 않은 칸의 글자색 — Theme.xaml 의 Sub 와 같다.</summary>
    private static readonly Color UnselectedTabColor = Color.FromRgb(0x64, 0x74, 0x8B);
}
