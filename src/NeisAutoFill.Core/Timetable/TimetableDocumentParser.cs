using System.Text.RegularExpressions;

namespace NeisAutoFill.Core.Timetable;

/// <summary>원본 문서에서 읽어낸 시간표 한 벌 (기술설계 §5 TimetableSourcePackage).</summary>
/// <param name="SchoolYear">학년도 (헤더에서)</param>
/// <param name="Semester">학기</param>
/// <param name="Lessons">날짜+교시별 수업</param>
/// <param name="Events">비고 칸의 행사 (날짜와 함께)</param>
/// <param name="Holidays">빨간 글씨로 적힌 공휴일·휴업일 (날짜 → 이름). 수업으로 넣지 않는다</param>
/// <param name="Warnings">해석하지 못한 것 — 조용히 버리지 않는다</param>
public sealed record TimetableSourcePackage(
    int SchoolYear,
    int Semester,
    IReadOnlyList<TimetableSourceLesson> Lessons,
    IReadOnlyList<(DateOnly Date, string Text)> Events,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<DateOnly, string>? Holidays = null)
{
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>빨간 글씨로 적힌 공휴일·휴업일. 파서가 채운다.</summary>
    public IReadOnlyDictionary<DateOnly, string> HolidayNames =>
        Holidays ?? new Dictionary<DateOnly, string>();
}

/// <summary>
/// 연간 시간표 문서(표)를 날짜+교시 수업 목록으로 옮긴다.
///
/// 표 구조(실측 2026-08-19):
/// <code>
/// 주 | 기 간 | 수업일수 |     월(1~7교시)     |     화(1~7)    | … |  비 고
///  1 | 3. 4- 3. 7 |  4  | 자 국 국 음 음 음 …
/// </code>
/// <b>글자 좌표로 열을 정한다.</b> 텍스트만 평탄화하면 학기 첫 주처럼 요일이 밀린 경우
/// 하루씩 어긋난 열에 배정된다 — 실제로 그 오류를 확인했다.
///
/// 날짜는 문서에 요일별로 적혀 있지 않으므로 <b>기간 시작일 + 열 순서</b>로 계산하고,
/// 그 결과가 실제로 그 요일인지 다시 확인한다.
/// </summary>
public static partial class TimetableDocumentParser
{
    [GeneratedRegex(@"(\d{4})\s*학년도")]
    private static partial Regex SchoolYearPattern();

    [GeneratedRegex(@"([12])\s*학기")]
    private static partial Regex SemesterPattern();

    /// <summary>"3. 4- 3. 7" · "3.10- 3.14" — 공백이 섞여 들어온다.</summary>
    [GeneratedRegex(@"(\d{1,2})\s*\.\s*(\d{1,2})\s*-\s*(\d{1,2})\s*\.\s*(\d{1,2})")]
    private static partial Regex PeriodRangePattern();

    /// <summary>비고의 "3.4(화) 시업식".</summary>
    [GeneratedRegex(@"(\d{1,2})\.(\d{1,2})\s*\(([월화수목금토일])\)\s*(.*)")]
    private static partial Regex EventPattern();

    private const int DaysPerWeek = 5;   // 월~금

    /// <summary>'기 간' 열의 반폭 — 이 범위 글자만 기간으로 읽는다.</summary>
    private const double RangeColumnHalfWidth = 26;

    /// <summary>문서 전체를 해석한다.</summary>
    public static TimetableSourcePackage Parse(DocumentLayout layout)
    {
        var warnings = new List<string>();
        var lessons = new List<TimetableSourceLesson>();
        var events = new List<(DateOnly, string)>();
        var holidays = new Dictionary<DateOnly, string>();

        var (year, semester) = ReadHeader(layout, warnings);

        foreach (var page in layout.Pages)
        {
            var lines = page.Lines();
            var (slots, headerY) = FindPeriodSlots(lines);
            if (slots.Count == 0) continue;   // 시간표 표가 없는 쪽 (요약표 등)

            var periodsPerDay = PeriodsPerDay(slots);
            if (slots.Count != DaysPerWeek * periodsPerDay)
                warnings.Add($"{page.Number}쪽: 교시 칸 {slots.Count}개가 월~금 {periodsPerDay}교시 배치와 맞지 않습니다.");

            var (rangeX, noteX) = FindSideColumns(lines, slots);

            // 표의 한 행은 여러 기준선에 걸쳐 있다 — 한글과 숫자의 baseline 이 다르고 비고는 여러 줄이다.
            // 그래서 "줄"이 아니라 <b>기간이 적힌 Y 를 기준으로 한 행 범위</b>로 글자를 모은다.
            var anchors = new List<(double Y, DateOnly Start)>();
            foreach (var line in lines)
            {
                var rangeText = DocumentPage.TextOf(
                    line.Where(g => Math.Abs(g.CenterX - rangeX) <= RangeColumnHalfWidth));

                var m = PeriodRangePattern().Match(rangeText);
                if (!m.Success) continue;

                var start = ToDate(year, semester,
                    int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                if (start is not null && InSemester(start.Value, semester))
                    anchors.Add((line.Max(g => g.Y), start.Value));
            }
            if (anchors.Count == 0) continue;

            anchors.Sort((a, b) => b.Y.CompareTo(a.Y));
            var rowHeight = RowHeight(anchors);

            for (var i = 0; i < anchors.Count; i++)
            {
                var (anchorY, weekStart) = anchors[i];
                // 위아래 이웃 행의 중간까지를 이 행의 범위로 — 마지막 행은 행 높이만큼
                var top = i == 0 ? anchorY + rowHeight : (anchors[i - 1].Y + anchorY) / 2;
                var bottom = i == anchors.Count - 1 ? anchorY - rowHeight : (anchorY + anchors[i + 1].Y) / 2;

                // 교시 헤더 줄을 행 범위가 삼키면 헤더 숫자가 수업으로 들어간다 — 헤더 아래만 본다
                var rowGlyphs = page.Glyphs
                    .Where(g => g.Y <= top && g.Y > bottom && g.Y < headerY - 1)
                    .ToList();

                ReadWeekRow(rowGlyphs, slots, periodsPerDay, noteX, weekStart,
                    lessons, holidays, warnings, page.Number);
                ReadEvents(rowGlyphs, noteX, year, semester, events);
            }
        }

        return new TimetableSourcePackage(year, semester, lessons, events, warnings, holidays);
    }

    /// <summary>헤더에서 학년도·학기. 못 찾으면 경고하고 0 을 돌려준다 — 사용자가 화면에서 고칠 수 있다.</summary>
    private static (int Year, int Semester) ReadHeader(DocumentLayout layout, List<string> warnings)
    {
        var head = string.Concat(layout.Pages.Take(1)
            .SelectMany(p => p.Lines().Take(6))
            .Select(DocumentPage.TextOf));

        var y = SchoolYearPattern().Match(head);
        var s = SemesterPattern().Match(head);

        if (!y.Success) warnings.Add("문서에서 학년도를 찾지 못했습니다.");
        if (!s.Success) warnings.Add("문서에서 학기를 찾지 못했습니다.");

        return (y.Success ? int.Parse(y.Groups[1].Value) : 0,
                s.Success ? int.Parse(s.Groups[1].Value) : 0);
    }

    /// <summary>
    /// 교시 헤더 줄에서 (요일, 교시) 칸의 X 좌표를 얻는다.
    /// 1~7 이 여러 번 반복되는 줄이 그 줄이다.
    /// </summary>
    private static (IReadOnlyList<(int Period, double X)> Slots, double HeaderY) FindPeriodSlots(
        IReadOnlyList<IReadOnlyList<TextGlyph>> lines)
    {
        foreach (var line in lines)
        {
            var digits = line.Where(g => g.Text.Length == 1 && g.Text[0] is >= '1' and <= '9').ToList();

            // 교시 헤더는 1부터 시작하는 묶음이 요일 수만큼 반복된다.
            // 마지막 교시 번호를 고정하지 않는다 — 6교시인 학교도 7교시인 학교도 있다.
            if (digits.Count(g => g.Text == "1") < DaysPerWeek - 1 || digits.Count < DaysPerWeek * 2) continue;

            return (digits.OrderBy(g => g.CenterX)
                          .Select(g => (Period: int.Parse(g.Text), X: g.CenterX))
                          .ToList(),
                    line.Min(g => g.Y));
        }
        return (Array.Empty<(int, double)>(), 0);
    }

    /// <summary>기간 열 중심과 비고 열 시작 X 를 헤더에서 찾는다. 못 찾으면 슬롯 기준으로 어림한다.</summary>
    private static (double RangeX, double NoteX) FindSideColumns(
        IReadOnlyList<IReadOnlyList<TextGlyph>> lines, IReadOnlyList<(int Period, double X)> slots)
    {
        var fallback = (slots[0].X - 25, slots[^1].X + SlotWidth(slots));

        foreach (var line in lines)
        {
            var left = line.Where(g => g.CenterX < slots[0].X).ToList();
            var right = line.Where(g => g.CenterX > slots[^1].X).ToList();

            var 기 = left.FirstOrDefault(g => g.Text == "기");
            var 간 = left.FirstOrDefault(g => g.Text == "간");
            var 비 = right.FirstOrDefault(g => g.Text == "비");

            if (기.Text is null || 간.Text is null || 비.Text is null) continue;
            return ((기.CenterX + 간.CenterX) / 2, 비.CenterX - 6);
        }
        return fallback;
    }

    /// <summary>
    /// 이 가로 좌표가 어느 교시 칸에 들어가는지. 범위를 벗어나면 -1.
    /// 왼쪽 좌표가 아니라 <b>중심</b>으로 본다 — 한글과 숫자는 폭이 달라 왼쪽으로 재면 칸이 밀린다.
    /// 오른쪽은 비고 열로 막는다 — 비고 글자가 마지막 교시 칸에 빨려 들어가는 일이 있다.
    /// </summary>
    private static int NearestSlot(
        double centerX, IReadOnlyList<(int Period, double X)> slots, double half, double noteX)
    {
        if (centerX < slots[0].X - half || centerX > Math.Min(slots[^1].X + half, noteX)) return -1;

        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < slots.Count; i++)
        {
            var d = Math.Abs(slots[i].X - centerX);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }

        return bestDistance > half ? -1 : best;
    }

    /// <summary>주차 한 줄의 글자를 가장 가까운 교시 칸에 배정한다.</summary>
    private static void ReadWeekRow(
        IReadOnlyList<TextGlyph> rowGlyphs, IReadOnlyList<(int Period, double X)> slots,
        int periodsPerDay, double noteX,
        DateOnly weekStart, List<TimetableSourceLesson> lessons,
        Dictionary<DateOnly, string> holidays, List<string> warnings, int pageNumber)
    {
        var half = SlotWidth(slots) / 2 + 1;   // 칸 경계 — 이웃 칸으로 새지 않게
        var buckets = new string?[slots.Count];
        var redBuckets = new string?[slots.Count];   // 빨간 글씨 = 공휴일·휴업일

        // 그림자 글꼴이 찍은 안 보이는 흰 겹은 버린다 — 그대로 두면 같은 글자가 겹쳐 들어온다
        rowGlyphs = rowGlyphs.Where(g => !g.IsInvisible).ToList();

        // 공휴일 글씨는 <b>기준선을 따지지 않고</b> 먼저 걷는다.
        // 수업 글자 위에 덧씌운 장식이라 기준선이 조금 달라, 아래 기준선 필터에 걸려 사라진다(실측).
        foreach (var g in rowGlyphs.Where(g => g.IsRed))
        {
            var slot = NearestSlot(g.CenterX, slots, half, noteX);
            if (slot >= 0) redBuckets[slot] = (redBuckets[slot] ?? "") + g.Text;
        }

        // 한 행 안에서도 수업 토큰은 <b>모두 같은 기준선</b>에 있다. 비고 칸의 여러 줄이 같은 X 범위로
        // 삐져 들어오는 일이 있어, 글자가 가장 많이 모인 기준선만 남긴다.
        var lessonLine = rowGlyphs
            .Where(g => g.CenterX >= slots[0].X - half && g.CenterX <= Math.Min(slots[^1].X + half, noteX))
            .GroupBy(g => Math.Round(g.Y))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        if (lessonLine is null) return;
        var baseline = lessonLine.Key;

        foreach (var g in rowGlyphs)
        {
            if (Math.Abs(Math.Round(g.Y) - baseline) > 1) continue;   // 다른 기준선 = 비고 등

            if (g.IsRed) continue;   // 공휴일은 위에서 이미 걷었다

            var best = NearestSlot(g.CenterX, slots, half, noteX);
            if (best < 0) continue;

            buckets[best] = (buckets[best] ?? "") + g.Text;
        }

        // 빨간 글씨가 있는 날은 통째로 공휴일이다.
        // "재량휴업일" 다섯 글자가 그 날의 교시 칸에 한 자씩 걸쳐 쓰이므로 날짜별로 이어 붙인다.
        var redDays = new HashSet<int>();
        for (var i = 0; i < redBuckets.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(redBuckets[i])) continue;

            var dayIndex = i / periodsPerDay;
            var date = weekStart.AddDays(dayIndex - DayOffset(weekStart));
            redDays.Add(dayIndex);

            holidays[date] = (holidays.TryGetValue(date, out var had) ? had : "") + redBuckets[i]!.Trim();
        }

        for (var i = 0; i < buckets.Length; i++)
        {
            var token = buckets[i]?.Trim();
            if (string.IsNullOrEmpty(token)) continue;

            var dayIndex = i / periodsPerDay;
            var period = slots[i].Period;
            var date = weekStart.AddDays(dayIndex - DayOffset(weekStart));

            // 공휴일인 날의 칸은 수업이 아니다 — 그림자 회색 겹이 남아 있어도 여기서 걸러진다
            if (redDays.Contains(dayIndex)) continue;

            if (token.Length > 2)
            {
                // 칸을 가로지르는 긴 글자. 수업으로 넣지 않고 알려만 준다.
                warnings.Add($"{pageNumber}쪽 {date:MM-dd} {period}교시: 수업 표기가 아닌 글자 '{token}' — 수업으로 넣지 않았습니다.");
                continue;
            }

            lessons.Add(new TimetableSourceLesson(new TimetableCell(date, period), token));
        }
    }

    /// <summary>비고 칸의 "3.4(화) 시업식" 을 날짜와 함께 모은다.</summary>
    private static void ReadEvents(
        IReadOnlyList<TextGlyph> rowGlyphs, double noteX,
        int year, int semester, List<(DateOnly, string)> events)
    {
        var text = DocumentPage.TextOf(rowGlyphs.Where(g => g.CenterX >= noteX));

        var m = EventPattern().Match(text);
        if (!m.Success) return;

        var date = ToDate(year, semester, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        var title = m.Groups[4].Value.Trim();
        if (date is not null && title.Length > 0) events.Add((date.Value, title));
    }

    /// <summary>
    /// 하루에 몇 교시인지 — 슬롯의 교시 번호가 다시 작아지는 지점이 다음 날이다.
    /// 학교마다 6교시일 수도 7교시일 수도 있어 고정하지 않는다.
    /// </summary>
    private static int PeriodsPerDay(IReadOnlyList<(int Period, double X)> slots)
    {
        for (var i = 1; i < slots.Count; i++)
            if (slots[i].Period <= slots[i - 1].Period) return i;

        return slots.Count;
    }

    /// <summary>
    /// 그 날짜가 해당 학기 안인지. 1학기 = 3~8월, 2학기 = 9~2월.
    /// 요약표의 숫자가 기간처럼 걸려 엉뚱한 주차가 생기는 것을 막는다.
    /// </summary>
    private static bool InSemester(DateOnly date, int semester) => semester switch
    {
        1 => date.Month is >= 3 and <= 8,
        2 => date.Month is >= 9 or <= 2,
        _ => true,   // 학기를 못 읽었으면 거르지 않는다
    };

    /// <summary>기간이 적힌 Y 들의 간격 중앙값 = 표 한 행의 높이.</summary>
    private static double RowHeight(IReadOnlyList<(double Y, DateOnly Start)> anchors)
    {
        if (anchors.Count < 2) return 20;

        var gaps = new List<double>();
        for (var i = 1; i < anchors.Count; i++) gaps.Add(anchors[i - 1].Y - anchors[i].Y);
        gaps.Sort();
        return gaps[gaps.Count / 2] / 2;
    }

    private static double SlotWidth(IReadOnlyList<(int Period, double X)> slots) =>
        slots.Count < 2 ? 8 : (slots[^1].X - slots[0].X) / (slots.Count - 1);

    /// <summary>기간 시작일이 그 주의 몇 번째 평일인지 (월=0). 첫 주는 화·수요일에 시작하기도 한다.</summary>
    private static int DayOffset(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;

    /// <summary>
    /// 학년도·학기와 월/일로 실제 날짜를 만든다. 말이 안 되는 값이면 null —
    /// 요약표의 숫자가 기간 형식처럼 걸리는 일이 있어 예외 대신 걸러낸다.
    /// 2학기의 1·2월은 <b>다음 해</b>다 (학년도는 3월에 시작).
    /// </summary>
    private static DateOnly? ToDate(int schoolYear, int semester, int month, int day)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        if (schoolYear <= 0) schoolYear = DateTime.Today.Year;

        var year = (semester == 2 && month <= 2) ? schoolYear + 1 : schoolYear;
        return day > DateTime.DaysInMonth(year, month) ? null : new DateOnly(year, month, day);
    }
}
