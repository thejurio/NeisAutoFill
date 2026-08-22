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
    /// <param name="columnName">
    /// 화면 열 → 실제 DataTable 컬럼명. null 이면 머리글을 그대로 쓴다.
    /// <b>머리글은 겹칠 수 있다</b> — 한 영역에 평가가 여럿이면 머리글이 같은 열이 여럿이다.
    /// </param>
    /// <param name="grow">
    /// (모자란 줄 수, 붙여넣기 시작 칸의 컬럼명) → 줄 늘리기. <c>allowGrow</c> 와 달리 <b>이 표 밖에서</b>
    /// 늘려야 할 때 쓴다 — 명단은 모든 과목 표에 같이 늘어나야 하기 때문이다.
    /// </param>
    /// <param name="dropHeaderRow">첫 줄이 컬럼 제목과 똑같으면 떼어 낸다 (엑셀에서 제목까지 복사한 경우).</param>
    public static (int Applied, int Skipped) Paste(
        DataGrid grid, DataTable table,
        Func<string, string, bool>? validate = null,
        bool allowGrow = false,
        Func<DataGridColumn, string>? columnName = null,
        Action<int, string>? grow = null,
        bool dropHeaderRow = false)
    {
        string Col(DataGridColumn c) => columnName?.Invoke(c) ?? c.Header?.ToString() ?? "";

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
                var name = Col(cell.Column);
                if (cell.Column.IsReadOnly || !table.Columns.Contains(name) ||
                    (validate is not null && !validate(name, value))) { refused++; continue; }
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

        // 엑셀에서 제목줄까지 함께 복사하는 일이 잦다 — 제목과 똑같은 첫 줄은 데이터가 아니다
        if (dropHeaderRow && rows.Length > 1 && IsHeaderRow(rows[0], columns, startCol))
        {
            rows = rows.Skip(1).ToArray();
            if (rows.Length == 0) return (0, 0);
        }

        // 표보다 긴 자료면 먼저 줄을 늘린다 (늘릴 수 없으면 아래에서 그냥 건너뛴다)
        if (grow is not null && startRow + rows.Length > table.Rows.Count && startCol < columns.Count)
            grow(startRow + rows.Length - table.Rows.Count, Col(columns[startCol]));

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
                var colName = Col(gridCol);
                if (gridCol.IsReadOnly || !table.Columns.Contains(colName)) { skipped++; continue; }

                var value = rows[r][c];
                if (validate is not null && !validate(colName, value)) { skipped++; continue; }

                table.Rows[rowIdx][colName] = value;
                applied++;
            }
        }
        return (applied, skipped);
    }

    /// <summary>붙여넣을 첫 줄이 컬럼 제목 그대로인가 (칸 하나라도 어긋나면 아니다).</summary>
    private static bool IsHeaderRow(string[] row, List<DataGridColumn> columns, int startCol)
    {
        if (row.Length == 0) return false;

        for (int c = 0; c < row.Length; c++)
        {
            int i = startCol + c;
            if (i >= columns.Count) return false;
            if ((columns[i].Header?.ToString() ?? "").Trim() != row[c].Trim()) return false;
        }
        return true;
    }
}
