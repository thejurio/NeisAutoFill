using System.Data;
using System.Windows;
using System.Windows.Controls;
using NeisAutoFill.Core;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// DataTable 기반 DataGrid 에 엑셀식 붙여넣기 (현재 셀 기준 우하향).
/// 읽기전용 컬럼은 건너뛰고, 검증 콜백이 거부한 값은 스킵으로 센다.
/// </summary>
public static class DataGridClipboard
{
    /// <param name="validate">(헤더, 값) → 허용 여부. null 이면 전부 허용.</param>
    /// <param name="allowGrow">붙여넣을 행이 표보다 많으면 새 행 추가 (명단·계획 편집용).</param>
    /// <param name="resolveColumn">표시 헤더 → 실제 DataTable 컬럼명 변환 (영역명↔안전ID). null 이면 헤더=컬럼명.</param>
    public static (int Applied, int Skipped) Paste(
        DataGrid grid, DataTable table,
        Func<string, string, bool>? validate = null,
        bool allowGrow = false,
        Func<string, string>? resolveColumn = null)
    {
        string Col(string header) => resolveColumn?.Invoke(header) ?? header;

        var rows = ClipboardTable.Parse(Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (rows.Length == 0) return (0, 0);

        var selected = grid.SelectedCells
            .Where(c => c.IsValid && c.Item is DataRowView && c.Column is not null)
            .ToList();

        // 값 하나를 여러 셀에 붙여넣기 → 엑셀처럼 선택된 셀 전체를 그 값으로 채운다
        if (rows.Length == 1 && rows[0].Length == 1 && selected.Count > 1)
        {
            var value = rows[0][0];
            int filled = 0, refused = 0;
            foreach (var cell in selected)
            {
                var header = cell.Column.Header?.ToString() ?? "";
                var name = Col(header);
                if (cell.Column.IsReadOnly || !table.Columns.Contains(name) ||
                    (validate is not null && !validate(header, value))) { refused++; continue; }
                ((DataRowView)cell.Item).Row[name] = value;
                filled++;
            }
            return (filled, refused);
        }

        // 붙여넣기 기준점: 선택 영역의 좌상단 (없으면 현재 셀, 그것도 없으면 표 끝)
        int startRow, startCol;
        if (selected.Count > 0)
        {
            startRow = selected.Min(c => table.Rows.IndexOf(((DataRowView)c.Item).Row));
            startCol = selected.Min(c => c.Column.DisplayIndex);
        }
        else
        {
            startRow = grid.CurrentCell.Item is DataRowView drv
                ? table.Rows.IndexOf(drv.Row)
                : table.Rows.Count;
            startCol = grid.CurrentCell.Column?.DisplayIndex ?? 0;
        }
        if (startRow < 0) startRow = table.Rows.Count;

        // 화면 컬럼 순서(DisplayIndex) → 헤더 문자열 (= DataTable 컬럼명)
        var columns = grid.Columns.OrderBy(c => c.DisplayIndex).ToList();

        int applied = 0, skipped = 0;
        for (int r = 0; r < rows.Length; r++)
        {
            int rowIdx = startRow + r;
            if (rowIdx >= table.Rows.Count)
            {
                if (!allowGrow) { skipped += rows[r].Length; continue; }
                table.Rows.Add(table.NewRow());
            }

            for (int c = 0; c < rows[r].Length; c++)
            {
                int colIdx = startCol + c;
                if (colIdx >= columns.Count) { skipped++; continue; }
                var gridCol = columns[colIdx];
                var header = gridCol.Header?.ToString() ?? "";
                var colName = Col(header);
                if (gridCol.IsReadOnly || !table.Columns.Contains(colName)) { skipped++; continue; }

                var value = rows[r][c];
                if (validate is not null && !validate(header, value)) { skipped++; continue; }

                table.Rows[rowIdx][colName] = value;
                applied++;
            }
        }
        return (applied, skipped);
    }
}
