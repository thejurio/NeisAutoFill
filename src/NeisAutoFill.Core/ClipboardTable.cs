namespace NeisAutoFill.Core;

/// <summary>
/// 엑셀 등에서 복사한 클립보드 텍스트(TSV)를 2차원 표로 파싱.
/// 엑셀 복사 규약: 행 구분 \r\n(마지막 행 뒤에도 붙음), 셀 구분 \t.
/// </summary>
public static class ClipboardTable
{
    /// <summary>TSV 텍스트 → 행×열 문자열 표. 빈 입력이면 빈 배열.</summary>
    public static string[][] Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string[]>();

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // 엑셀은 마지막 행 뒤에 개행을 붙인다 — 끝의 빈 행만 제거 (중간 빈 행은 유지)
        int end = lines.Length;
        while (end > 0 && lines[end - 1].Length == 0) end--;

        var rows = new string[end][];
        for (int i = 0; i < end; i++)
            rows[i] = lines[i].Split('\t').Select(s => s.Trim()).ToArray();
        return rows;
    }

    /// <summary>
    /// 표를 (번호, 이름) 명단으로 해석.
    /// 2열 이상이고 첫 열이 숫자면 번호|이름, 아니면 첫 열을 이름으로 보고 번호는 순번 자동 부여.
    /// "번호"/"이름" 헤더 행은 건너뛴다.
    /// </summary>
    public static IReadOnlyList<(string No, string Name)> ToRoster(string[][] rows)
    {
        var result = new List<(string, string)>();
        foreach (var row in rows)
        {
            if (row.Length == 0) continue;
            var first = row[0];
            var second = row.Length > 1 ? row[1] : "";
            if (first is "번호" or "이름" || second == "이름") continue;   // 헤더 행

            string no, name;
            if (row.Length >= 2 && second != "" && int.TryParse(first, out _))
            {
                no = first; name = second;
            }
            else
            {
                no = ""; name = first != "" ? first : second;
            }
            if (name == "") continue;
            result.Add((no, name));
        }
        // 번호 없는 항목은 순번 자동 부여
        for (int i = 0; i < result.Count; i++)
            if (result[i].Item1 == "")
                result[i] = ((i + 1).ToString(), result[i].Item2);
        return result;
    }
}
