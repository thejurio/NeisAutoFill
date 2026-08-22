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

        // 오른쪽 단추로는 WPF 가 칸을 고르지 않는다 — 메뉴가 무엇을 대상으로 하는지 보이도록 직접 고른다.
        // 이미 고른 칸 위에서 눌렀으면 그 선택을 그대로 둔다(엑셀과 같은 방식).
        grid.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (FindAncestor<DataGridCell>(e.OriginalSource) is not { Column: not null } cell ||
                cell.DataContext is not DataRowView row) return;

            if (grid.SelectedCells.Any(c => ReferenceEquals(c.Item, row) &&
                                            ReferenceEquals(c.Column, cell.Column))) return;

            grid.SelectedCells.Clear();
            grid.CurrentCell = new DataGridCellInfo(row, cell.Column);
            grid.SelectedCells.Add(grid.CurrentCell);
        };

        // 이름 없이 생긴 줄은 그 줄을 벗어나는 순간 없앤다 (아래 _pending 설명)
        grid.CurrentCellChanged += (_, _) =>
        {
            if (_pending is not null && !ReferenceEquals(grid.CurrentCell.Item, _pending))
                DropPendingIfEmpty(grid, commit: true);
        };
        grid.AddHandler(UIElement.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler((_, e) =>
            {
                // 표 안에서 옮겨 다니는 것은 위 CurrentCellChanged 가 본다 — 여기선 표 밖으로 나갈 때만
                if (e.NewFocus is DependencyObject next && IsInside(next, grid)) return;
                DropPendingIfEmpty(grid, commit: true);
            }), handledEventsToo: true);
    }

    // ── 명단 (번호·이름) ────────────────────────

    /// <summary>지금 고르고 있는 칸이 표의 <b>맨 아랫줄</b>인가.</summary>
    private static bool OnLastRow(DataGrid grid)
    {
        if (grid.Items.Count == 0) return false;

        var row = grid.CurrentCell.Item;

        return row is not null && ReferenceEquals(row, grid.Items[^1]);
    }

    /// <summary>학생 한 명을 더하고 그 줄 <b>이름 칸</b>으로 옮겨 간다 — 바로 이어서 칠 수 있게.</summary>
    public void AddStudentRow(DataGrid grid)
    {
        grid.CommitEdit(DataGridEditingUnit.Row, true);

        // 이름을 안 쓴 빈 줄이 이미 기다리고 있으면 또 만들지 않는다 — Enter 를 두 번 쳐도 한 줄만
        if (_pending is DataRowView waiting && grid.Items.Contains(waiting) && IsNameless(waiting)) return;

        if (main.AddStudent() < 0) return;

        grid.UpdateLayout();
        if (grid.Items.Count == 0) return;

        var last = grid.Items[^1];
        var nameColumn = grid.Columns.FirstOrDefault(c => c.Header?.ToString() == "이름");
        if (nameColumn is null) return;

        grid.ScrollIntoView(last, nameColumn);
        grid.CurrentCell = new DataGridCellInfo(last, nameColumn);
        grid.SelectedCells.Clear();
        grid.SelectedCells.Add(grid.CurrentCell);
        grid.BeginEdit();

        // 이름이 채워지기 전까지는 임시 — 여기서 표시해 두어야 벗어날 때 지울 수 있다.
        // (CurrentCell 을 옮기는 위 코드가 이전 임시 줄의 운명을 이미 결정했다)
        _pending = last;
    }

    /// <summary>단추로 학생 더하기 — 표 맨 아랫줄 Enter 와 같은 일을 한다.</summary>
    public void AddStudent() { if (_active is not null) AddStudentRow(_active); }

    /// <summary>
    /// 표 밖에 초점이 있을 때 온 Ctrl+Z. 표 안에서 온 것은 이미 처리되고 여기까지 오지 않는다.
    /// 명단을 지우고 다른 곳을 눌러 버리면 되돌릴 길이 없었다(2026-08-22).
    /// </summary>
    public void UndoActive() { if (_active is not null) Undo(_active); }

    // ── 일괄 입력 바 (버튼) ─────────────────────

    public void BulkAssign(string label) { if (_active is not null) ApplyToSelected(_active, label); }
    public void BulkClear() { if (_active is not null) ApplyToSelected(_active, ""); }

    public void SelectAll()
    {
        if (_active is null) return;
        _active.Focus();

        // '전체 선택' = 등급(영역) 셀만 — 번호·이름·특기사항은 일괄 입력 대상이 아니므로 제외
        if (_active.DataContext is not SubjectViewModel vm) { _active.SelectAllCells(); return; }
        var areaCols = _active.Columns
            .Where(c => vm.Areas.Contains(c.Header?.ToString() ?? "")).ToList();
        if (areaCols.Count == 0) { _active.SelectAllCells(); return; }   // 영역이 없으면 기존 동작

        _active.SelectedCells.Clear();
        foreach (var item in _active.Items)
        {
            if (item is not DataRowView) continue;
            foreach (var col in areaCols)
                _active.SelectedCells.Add(new DataGridCellInfo(item, col));
        }
    }

    // ── 키보드 ─────────────────────────────

    public void OnPreviewKeyDown(DataGrid grid, KeyEventArgs e)
    {
        // <b>맨 아랫줄에서 Enter = 학생 한 명 더.</b>
        // 아래로 내려갈 자리가 없는 곳이라, 거기서 Enter 는 "다음 줄을 만든다"로 읽는 것이 자연스럽다.
        // 셀을 고치는 중(TextBox)에도 통해야 해서 아래 조기 반환보다 먼저 본다.
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && OnLastRow(grid))
        {
            // <b>빈 줄에서 한 번 더 Enter = 그만.</b> Esc 와 같이 그 줄을 없앤다(사용자 요청 2026-08-22).
            // 이름을 썼으면 지금까지대로 다음 학생 줄로 이어진다.
            if (_pending is DataRowView waiting && ReferenceEquals(grid.CurrentCell.Item, waiting))
            {
                grid.CommitEdit(DataGridEditingUnit.Row, true);   // 치던 글자를 살려 놓고 비었는지 본다
                if (IsNameless(waiting))
                {
                    DropPendingIfEmpty(grid, commit: false);
                    e.Handled = true;
                    return;
                }
            }

            AddStudentRow(grid);
            e.Handled = true;
            return;
        }

        // <b>Esc = 방금 생긴 빈 줄 취소.</b> 편집 중(TextBox)에도 와야 해서 아래 조기 반환보다 먼저 본다.
        if (e.Key == Key.Escape && _pending is not null &&
            ReferenceEquals(grid.CurrentCell.Item, _pending))
        {
            DropPendingIfEmpty(grid, commit: false);
            e.Handled = true;
            return;
        }

        // 셀 편집기(콤보·텍스트박스) 안에서 입력 중이면 개입하지 않는다
        if (e.OriginalSource is TextBox or ComboBox) return;

        // ` (백쿼트) = 전체 선택 — 숫자키 일괄 입력과 조합해 키보드만으로 완결
        if (e.Key == Key.Oem3 && Keyboard.Modifiers == ModifierKeys.None)
        {
            SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Paste(grid);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo(grid);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Delete)
        {
            // 번호·이름 칸이 잡혀 있으면 그 학생을 명단에서 뺀다. 등급 칸이면 지금까지대로 값만 지운다.
            if (!RemoveSelectedStudents(grid)) ApplyToSelected(grid, "");
            e.Handled = true;
            return;
        }

        // 숫자 1~9 = 척도 단계 지정 (단일 셀 포함). 선택에 등급(영역) 셀이 없으면
        // 개입하지 않는다 — 특기사항 셀에 숫자 타이핑으로 편집 시작하는 흐름 보존.
        int digit = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1 + 1,
            _ => 0,
        };
        if (digit > 0 && Keyboard.Modifiers == ModifierKeys.None &&
            grid.DataContext is SubjectViewModel vd &&
            grid.SelectedCells.Any(c => vd.Areas.Contains(c.Column?.Header?.ToString() ?? "")))
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
            // <b>한 번 = 그 학생 전체 선택, 두 번 = 이름 고치기.</b>
            // 한 번 누르기로 가로 한 줄이 잡히는 건 자주 쓰는 기능이라 그대로 두고,
            // 이름을 고칠 길만 두 번 누르기로 따로 냈다(사용자 요청 2026-08-22).
            if (e.ClickCount >= 2)
            {
                // 첫 번째 누르기로 잡힌 가로 한 줄은 푼다 — 이름을 고치는 중에 남아 있으면
                // 무엇을 하고 있는지 헷갈린다(사용자 지적 2026-08-22).
                grid.SelectedCells.Clear();
                grid.CurrentCell = new DataGridCellInfo(row, cell.Column);
                grid.BeginEdit();
                e.Handled = true;
                return;
            }

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

        // 번호·이름 칸을 고른 채 열었으면 '지우기'는 값이 아니라 <b>학생을 빼는</b> 일이 된다
        var students = SelectedStudentRows(grid);
        if (students.Count > 0)
        {
            var drop = new MenuItem
            {
                Header = $"학생 {students.Count}명 명단에서 빼기",
                InputGestureText = "Del",
            };
            drop.Click += (_, _) => RemoveSelectedStudents(grid);
            menu.Items.Add(drop);
        }
        else
        {
            var clear = new MenuItem { Header = "선택 셀 지우기", InputGestureText = "Del" };
            clear.Click += (_, _) => ApplyToSelected(grid, "");
            menu.Items.Add(clear);
        }
    }

    /// <summary>
    /// 고른 칸 가운데 번호·이름 칸이 있는 줄 번호들. 없으면 빈 목록.
    /// 번호는 <b>표(DataTable) 기준</b>이다 — 화면 정렬이 바뀌어도 엉뚱한 학생을 지우지 않는다.
    /// </summary>
    private static List<int> SelectedStudentRows(DataGrid grid) => grid.SelectedCells
        .Where(c => c.IsValid && c.Item is DataRowView &&
                    c.Column?.Header?.ToString() is "번호" or "이름")
        .Select(c => TableIndex((DataRowView)c.Item))
        .Where(i => i >= 0)
        .Distinct().OrderBy(i => i).ToList();

    private static int TableIndex(DataRowView view) => view.Row.Table.Rows.IndexOf(view.Row);

    /// <summary>고른 학생들을 <b>모든 과목에서</b> 뺀다. 뺐으면 true.</summary>
    private bool RemoveSelectedStudents(DataGrid grid)
    {
        var rows = SelectedStudentRows(grid);
        if (rows.Count == 0) return false;

        grid.CommitEdit(DataGridEditingUnit.Row, true);
        _pending = null;   // 임시 줄을 직접 빼는 경우 — 이중으로 지우지 않게

        var column = grid.CurrentCell.Column
                     ?? grid.Columns.FirstOrDefault(c => c.Header?.ToString() == "번호");
        var at = rows[0];

        var removed = main.RemoveStudents(rows);
        if (removed == 0) return false;

        // <b>지운 뒤에도 표에 초점을 남긴다.</b> Ctrl+Z 는 이 표의 PreviewKeyDown 으로만 오는데,
        // 오른쪽 단추 메뉴로 지우면 초점이 표 밖으로 나가 되돌리기가 먹히지 않았다(2026-08-22).
        grid.UpdateLayout();
        if (grid.Items.Count > 0 && column is not null)
        {
            grid.CurrentCell = new DataGridCellInfo(grid.Items[Math.Min(at, grid.Items.Count - 1)], column);
            grid.SelectedCells.Clear();
            grid.SelectedCells.Add(grid.CurrentCell);
        }
        grid.Focus();

        main.Log($"학생 {removed}명을 명단에서 뺐습니다 (Ctrl+Z 로 되돌릴 수 있습니다)");
        return true;
    }

    /// <summary>
    /// Ctrl+Z. 칸 편집과 명단 조작 가운데 <b>더 최근 것</b>을 되돌린다 —
    /// 둘은 따로 쌓이므로 한 곳에서 뽑은 번호표로 차례를 가린다.
    /// </summary>
    private void Undo(DataGrid grid)
    {
        var cells = grid.DataContext as SubjectViewModel;
        var roster = main.RosterUndoSeq;

        if (roster is not null && (cells?.UndoSeq is not { } seq || roster > seq))
        {
            if (main.UndoRoster() is { } what) main.Log($"↩ {what}");
            return;
        }

        if (cells is not null && cells.Undo()) { main.Log("↩ 실행 취소"); return; }

        // 조용히 아무 일도 안 하면 고장으로 보인다 — 되돌릴 게 없으면 없다고 말한다
        main.Log("되돌릴 것이 없습니다.");
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

    /// <summary>
    /// 클립보드 표 붙여넣기. 등급 셀은 척도 라벨만 허용. Ctrl+Z 한 번에 되돌려짐.
    ///
    /// <b>명단(번호·이름)도 받는다.</b> 번호나 이름 칸에서 붙여넣으면 학급 명단을 통째로 넣을 수 있고,
    /// 표보다 자료가 길면 모자란 만큼 <b>모든 과목에</b> 학생을 더한다(사용자 요청 2026-08-22).
    /// 등급 칸에서 시작한 붙여넣기는 줄을 늘리지 않는다 — 이름 없는 학생이 생기면 안 되기 때문이다.
    /// </summary>
    private void Paste(DataGrid grid)
    {
        if (grid.DataContext is not SubjectViewModel vm) return;
        vm.BeginBulkEdit();
        int applied, skipped, added = 0;
        try
        {
            (applied, skipped) = DataGridClipboard.Paste(grid, vm.Grid,
                validate: (header, value) =>
                    header is "번호" or "이름" ||   // 명단 칸은 값을 가리지 않는다
                    header == SubjectViewModel.NoteColumn || value == "" || main.GradeLabels.Contains(value),
                resolveColumn: vm.DataColumnOf,   // 영역 헤더 → 안전 컬럼ID
                grow: (need, startHeader) =>
                {
                    if (startHeader is not ("번호" or "이름")) return;
                    if (main.AddStudents(need) >= 0) added = need;   // Ctrl+Z 한 번에 되돌려지도록 한 묶음
                },
                dropHeaderRow: true);
        }
        finally { vm.EndBulkEdit(); }
        if (applied == 0 && skipped == 0) return;
        main.Log($"붙여넣기: {applied}셀 적용"
            + (added > 0 ? $", 학생 {added}명 추가" : "")
            + (skipped > 0 ? $", {skipped}셀 건너뜀 (허용외 등급·읽기전용)" : ""));
    }

    // ── 이름 없이 생긴 줄 되돌리기 ────────────────
    //
    // 맨 아랫줄에서 Enter 는 <b>"이 이름 확정"</b> 뜻으로 치는 경우가 많다(사용자 지적 2026-08-22).
    // 그때 딸려 나온 줄을 그대로 두면 이름 없는 학생이 명단에 남는다. 그래서 새로 생긴 줄은
    // 이름이 채워지기 전까지 <b>임시</b>로 두고, 이름 없이 그 줄을 벗어나면(Esc·다른 칸·표 밖) 없앤다.
    private object? _pending;
    private bool _dropping;

    private static bool IsNameless(DataRowView row) =>
        row.Row.RowState != DataRowState.Detached &&
        (row.Row["이름"]?.ToString() ?? "").Trim().Length == 0;

    /// <summary>임시 줄이 이름 없이 남았으면 없앤다. 이름이 들어갔으면 진짜 학생으로 굳힌다.</summary>
    /// <param name="commit">치던 글자를 살릴지(다른 칸으로 이동) 버릴지(Esc).</param>
    private void DropPendingIfEmpty(DataGrid grid, bool commit)
    {
        if (_dropping || _pending is not DataRowView row) return;

        _dropping = true;   // 아래 편집 종료·행 삭제가 같은 처리를 다시 부르는 것을 막는다
        try
        {
            _pending = null;

            if (commit) grid.CommitEdit(DataGridEditingUnit.Row, true);
            else grid.CancelEdit(DataGridEditingUnit.Row);

            var index = TableIndex(row);
            if (index < 0 || !IsNameless(row)) return;

            main.RemoveStudents(new[] { index }, undoable: false);
        }
        finally { _dropping = false; }
    }

    private static bool IsInside(DependencyObject node, DependencyObject root)
    {
        for (var at = node; at is not null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            if (ReferenceEquals(at, root)) return true;
        return false;
    }

    private static T? FindAncestor<T>(object source) where T : DependencyObject
    {
        var current = source as DependencyObject;
        while (current is not null and not T)
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return current as T;
    }
}
