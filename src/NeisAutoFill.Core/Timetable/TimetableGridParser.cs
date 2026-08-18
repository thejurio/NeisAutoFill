namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 시간표 그리드를 읽어낸 결과 (기술설계 §9 실측 기준).
/// </summary>
/// <param name="Cells">셀 주소 → 화면에 표시된 원문. 빈 셀은 빈 문자열로 들어온다</param>
/// <param name="Dates">이 주에 실제로 존재하는 날짜들 (열 순서)</param>
/// <param name="Columns">화면 열 번호(1부터) → 그 열의 날짜. 클릭 좌표를 구할 때 쓴다</param>
/// <param name="Warnings">해석하지 못한 행·열 — 조용히 버리지 않고 사용자에게 보고한다</param>
public sealed record TimetableGridSnapshot(
    IReadOnlyDictionary<TimetableCell, string> Cells,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyDictionary<int, DateOnly> Columns,
    IReadOnlyList<string> Warnings)
{
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>그 날짜가 놓인 화면 열 번호. 없으면 -1.
    /// <b>날짜로 열을 되찾아야 한다</b> — 필드 접두사(mon/tue…)는 요일이 아니라 열 순서일 뿐이다.</summary>
    public int ColumnOf(DateOnly date) =>
        Columns.FirstOrDefault(kv => kv.Value == date, new KeyValuePair<int, DateOnly>(-1, default)).Key;
}

/// <summary>
/// 나이스 시간표 그리드(grdWeekByClsByTi)의 CLX 행 데이터를 셀 주소로 옮긴다 (기술설계 §9).
///
/// 실측된 행 필드: <c>pir</c>(교시) 와 열별 <c>{day}Ymd</c>(날짜) · <c>{day}Otpt</c>(표시값).
///
/// ⚠ <b>mon/tue/… 접두사는 요일이 아니라 "몇 번째 열"이다</b> (2026-08-19 실측).
/// 주차가 수요일에 시작하면 <c>monYmd</c> 에 수요일 날짜가 들어온다. 요일로 믿고 계산하면
/// 엉뚱한 날에 입력하게 된다 — 날짜는 오직 <c>{day}Ymd</c> 값에서만 얻는다.
/// <b>aria-rowcount/colcount 로 행·열을 세지 않는다</b> — 복합 헤더 때문에 논리 데이터 수와 다르다.
/// 날짜는 헤더가 아니라 각 행의 Ymd 값에서 얻고, 헤더 날짜와는 <see cref="Verify"/> 로 교차 확인한다.
///
/// 브라우저 없이 테스트할 수 있도록 <b>이미 뽑아낸 값</b>만 받는다. 실제 수집은 Automation 계층의 몫이다.
/// </summary>
public static class TimetableGridParser
{
    /// <summary>열 순서대로의 필드 접두사 7개. <b>이름이 요일처럼 보이지만 요일이 아니다</b>(위 설명 참고).</summary>
    private static readonly string[] ColumnPrefixes = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };

    /// <summary>CLX 행 목록을 셀 지도로 변환한다. 각 행은 필드명 → 값 사전이다.</summary>
    public static TimetableGridSnapshot Parse(IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        var cells = new Dictionary<TimetableCell, string>();
        var dates = new SortedSet<DateOnly>();
        var columns = new Dictionary<int, DateOnly>();
        var warnings = new List<string>();

        var rowIndex = -1;
        foreach (var row in rows)
        {
            rowIndex++;

            if (!TryPeriod(row, out var period))
            {
                warnings.Add($"{rowIndex}번째 행에서 교시(pir)를 읽지 못했습니다.");
                continue;
            }

            for (var i = 0; i < ColumnPrefixes.Length; i++)
            {
                var prefix = ColumnPrefixes[i];
                var column = i + 1;   // 0열은 교시 라벨

                if (!row.TryGetValue(prefix + "Ymd", out var ymd) || string.IsNullOrWhiteSpace(ymd))
                    continue;   // 그 열이 없는 화면도 있다 (토·일 미표시 등) — 경고할 일은 아니다

                if (!TryDate(ymd, out var date))
                {
                    warnings.Add($"{period}교시 {column}열의 날짜를 해석하지 못했습니다: {ymd}");
                    continue;
                }

                // 같은 열에 행마다 다른 날짜가 들어오면 화면 해석이 어긋난 것이다 — 이건 진짜 경고감
                if (columns.TryGetValue(column, out var known) && known != date)
                    warnings.Add($"{column}열의 날짜가 행마다 다릅니다: {known:MM-dd} vs {date:MM-dd}");
                columns[column] = date;

                row.TryGetValue(prefix + "Otpt", out var text);
                // Otpt 는 HTML 이다 — 태그를 걷어내지 않으면 과목명이 통째로 어긋난다(실측 §4-A)
                cells[new TimetableCell(date, period)] = TimetableTextNormalizer.StripMarkup(text);
                dates.Add(date);
            }
        }

        return new TimetableGridSnapshot(cells, dates.ToList(), columns, warnings);
    }

    /// <summary>
    /// 헤더에서 읽은 날짜와 행 데이터의 날짜가 같은지 교차 확인한다 (실기기검증 V3).
    /// 다르면 화면 해석이 어긋난 것이므로 입력을 진행하면 안 된다.
    /// </summary>
    public static IReadOnlyList<string> Verify(TimetableGridSnapshot snapshot, IEnumerable<DateOnly> headerDates)
    {
        var header = headerDates.ToHashSet();
        var problems = new List<string>();

        foreach (var d in snapshot.Dates.Where(d => !header.Contains(d)))
            problems.Add($"행 데이터에는 {d:yyyy-MM-dd} 이 있는데 헤더에는 없습니다.");

        foreach (var d in header.Where(d => !snapshot.Dates.Contains(d)))
            problems.Add($"헤더에는 {d:yyyy-MM-dd} 이 있는데 행 데이터에는 없습니다.");

        return problems;
    }

    private static bool TryPeriod(IReadOnlyDictionary<string, string> row, out int period)
    {
        period = 0;
        if (!row.TryGetValue("pir", out var raw) || string.IsNullOrWhiteSpace(raw)) return false;

        // "3" 도 "3교시" 도 올 수 있다 — 앞쪽 숫자만 취한다
        var digits = new string(raw.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out period) && period > 0;
    }

    private static bool TryDate(string ymd, out DateOnly date) =>
        DateOnly.TryParseExact(ymd.Trim(), "yyyyMMdd", out date);
}
