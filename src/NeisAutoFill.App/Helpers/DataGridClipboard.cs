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
    /// <param name="validate">(컬럼명, 값) → 허용 여부. null 이면 전부 허용.</param>
    /// <param name="allowGrow">붙여넣을 행이 표보다 많으면 새 행 추가 (명단·계획 편집용).</param>
    public static (int Applied, int Skipped) Paste(
        DataGrid grid, DataTable table,
        Func<string, string, bool>? validate = null,
        bool allowGrow = false)
    {
        var rows = ClipboardTable.Parse(Clipboard.ContainsText() ? Clipboard.GetText() : null);
        if (rows.Length == 0) return (0, 0);

        // 붙여넣기 기준점 (현재 셀). 새 행 플레이스홀더면 마지막 행 다음부터.
        int startRow = grid.CurrentCell.Item is DataRowView drv
            ? table.Rows.IndexOf(drv.Row)
            : table.Rows.Count;
        if (startRow < 0) startRow = table.Rows.Count;
        int startCol = grid.CurrentCell.Column?.DisplayIndex ?? 0;

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
                var colName = gridCol.Header?.ToString() ?? "";
                if (gridCol.IsReadOnly || !table.Columns.Contains(colName)) { skipped++; continue; }

                var value = rows[r][c];
                if (validate is not null && !validate(colName, value)) { skipped++; continue; }

                table.Rows[rowIdx][colName] = value;
                applied++;
            }
        }
        return (applied, skipped);
    }
}
