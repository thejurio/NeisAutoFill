using System.Text.RegularExpressions;

namespace NeisAutoFill.Core.Timetable;

/// <summary>창체 계획 문서에서 읽어낸 결과.</summary>
/// <param name="SchoolYear">학년도</param>
/// <param name="Semester">학기</param>
/// <param name="Events">일정들 (병합 전 원본 그대로)</param>
/// <param name="Warnings">해석하지 못한 것</param>
public sealed record CreativeSourcePackage(
    int SchoolYear,
    int Semester,
    IReadOnlyList<CreativeActivityEvent> Events,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 창의적 체험활동 연간지도계획(표)을 일정 목록으로 옮긴다 (기술설계 §8).
///
/// 표 구조(실측 2026-08-19):
/// <code>
/// 주 | 날 짜 | 요일 | 학습주제 | 활동내용 | 차시 | 영역 | 비고
///  1 |  3.3  |  화  | ◆시업식  | ◆시업식(자율자치활동) | 1/1 | 자율자치활동 | 생활안전
/// </code>
/// 한 파일에 자율·자치활동 / 동아리활동 / 진로활동 표가 이어져 나온다.
/// 종류는 <b>'영역' 칸</b>에서 읽는다 — 표 제목보다 행마다 적힌 값이 정확하다.
///
/// 열 경계는 헤더 라벨 사이의 <b>중점</b>으로 잡는다. 라벨은 칸 가운데 있어서
/// 라벨 X 를 그대로 경계로 쓰면 데이터가 옆 칸으로 넘어간다.
/// </summary>
public static partial class CreativeDocumentParser
{
    [GeneratedRegex(@"(\d{4})\s*학년도")]
    private static partial Regex SchoolYearPattern();

    [GeneratedRegex(@"([12])\s*학기")]
    private static partial Regex SemesterPattern();

    /// <summary>"3.3" · "3.16" — 날짜 칸.</summary>
    [GeneratedRegex(@"^(\d{1,2})\.(\d{1,2})$")]
    private static partial Regex DatePattern();

    public static CreativeSourcePackage Parse(DocumentLayout layout)
    {
        var warnings = new List<string>();
        var events = new List<CreativeActivityEvent>();
        var (year, semester) = ReadHeader(layout, warnings);

        foreach (var page in layout.Pages)
        {
            foreach (var table in FindTables(page))
                ReadTable(page, table, year, semester, events, warnings);
        }

        if (events.Count == 0) warnings.Add("창체 표를 찾지 못했습니다.");
        return new CreativeSourcePackage(year, semester, events, warnings);
    }

    /// <summary>한 쪽에 표가 여러 개 있을 수 있다(자율·동아리·진로). 헤더 줄마다 하나씩.</summary>
    private static IEnumerable<TableLayout> FindTables(DocumentPage page)
    {
        var lines = page.Lines();

        for (var i = 0; i < lines.Count; i++)
        {
            var text = DocumentPage.TextOf(lines[i]);
            if (!text.Contains("요일") || !text.Contains("차시") || !text.Contains("영역")) continue;

            var 주 = FindLabel(lines[i], "주");
            var 요일 = FindLabel(lines[i], "요일");
            var 차시 = FindLabel(lines[i], "차시");
            var 영역 = FindLabel(lines[i], "영역");
            var 주제 = FindLabel(lines[i], "학습주제");
            var 내용 = FindLabel(lines[i], "활동내용");
            if (요일 is null || 차시 is null || 영역 is null) continue;

            // 다음 헤더가 나오기 전까지가 이 표의 범위
            var bottom = double.MinValue;
            for (var j = i + 1; j < lines.Count; j++)
            {
                var t = DocumentPage.TextOf(lines[j]);
                if (t.Contains("요일") && t.Contains("차시") && t.Contains("영역"))
                {
                    bottom = lines[j].Max(g => g.Y);
                    break;
                }
            }

            yield return new TableLayout(
                Top: lines[i].Min(g => g.Y),
                Bottom: bottom,
                // 주차 번호가 날짜에 붙어 "13.3"(13월)이 되지 않도록 왼쪽 경계를 '주' 라벨로 막는다
                DateLeft: 주 is not null ? 주.Value + 8 : double.MinValue,
                DayX: 요일.Value,
                // 학습주제 칸은 요일 칸 바로 오른쪽부터. 라벨 중점을 쓰면 데이터가 라벨보다
                // 왼쪽에서 시작해 앞글자가 잘린다("교육활동…" → "육활동…").
                TopicLeft: 요일.Value + 10,
                // 학습주제와 활동내용 사이
                ContentLeft: 주제 is not null && 내용 is not null ? Mid(주제.Value, 내용.Value) : 차시.Value - 60,
                CountLeft: Mid(내용 ?? 차시.Value - 60, 차시.Value),
                KindLeft: Mid(차시.Value, 영역.Value),
                NoteLeft: 영역.Value + 30);
        }
    }

    private static void ReadTable(
        DocumentPage page, TableLayout table, int year, int semester,
        List<CreativeActivityEvent> events, List<string> warnings)
    {
        var rows = page.Lines()
            .Where(l => l.Max(g => g.Y) < table.Top && l.Max(g => g.Y) > table.Bottom)
            .ToList();

        foreach (var row in rows)
        {
            // 날짜 칸 = 요일 칸 왼쪽
            var dateText = DocumentPage.TextOf(
                row.Where(g => g.CenterX > table.DateLeft && g.CenterX < table.DayX - 8)).Trim();
            var m = DatePattern().Match(dateText);
            if (!m.Success) continue;   // 데이터 행이 아니다

            var date = ToDate(year, semester, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
            if (date is null) continue;

            var kindText = DocumentPage.TextOf(
                row.Where(g => g.CenterX >= table.KindLeft && g.CenterX < table.NoteLeft)).Trim();
            var kind = ToKind(kindText);

            var topic = DocumentPage.TextOf(
                row.Where(g => g.CenterX >= table.TopicLeft && g.CenterX < table.ContentLeft)).Trim();
            var content = DocumentPage.TextOf(
                row.Where(g => g.CenterX >= table.ContentLeft && g.CenterX < table.CountLeft)).Trim();

            var name = topic.Length > 0 ? topic : content;
            if (name.Length == 0) continue;

            if (kind == CreativeActivityKind.Unresolved && kindText.Length > 0)
                warnings.Add($"{date:MM-dd} '{name}': 영역 '{kindText}' 을 알 수 없어 미분류로 두었습니다.");

            events.Add(new CreativeActivityEvent(
                date.Value, Period: null, kind, Clean(name),
                CreativeSourceKind.Detail, $"p{page.Number}"));
        }
    }

    /// <summary>활동명에서 장식 기호와 여분 공백을 걷어낸다 — 중복 판정의 정확도를 높인다.</summary>
    private static string Clean(string name) =>
        name.Replace("◆", "").Replace("◇", "").Replace("*", "").Trim();

    private static CreativeActivityKind ToKind(string text)
    {
        var n = TimetableTextNormalizer.Normalize(text);
        if (n.Contains(TimetableTextNormalizer.Normalize("자율"))) return CreativeActivityKind.Autonomy;
        if (n.Contains(TimetableTextNormalizer.Normalize("동아리"))) return CreativeActivityKind.Club;
        if (n.Contains(TimetableTextNormalizer.Normalize("진로"))) return CreativeActivityKind.Career;
        return CreativeActivityKind.Unresolved;   // 봉사활동 등 — 사용자가 정한다
    }

    private static (int Year, int Semester) ReadHeader(DocumentLayout layout, List<string> warnings)
    {
        var head = string.Concat(layout.Pages
            .SelectMany(p => p.Lines().Take(3))
            .Select(DocumentPage.TextOf));

        var y = SchoolYearPattern().Match(head);
        var s = SemesterPattern().Match(head);

        if (!y.Success) warnings.Add("창체 문서에서 학년도를 찾지 못했습니다.");
        if (!s.Success) warnings.Add("창체 문서에서 학기를 찾지 못했습니다.");

        return (y.Success ? int.Parse(y.Groups[1].Value) : 0,
                s.Success ? int.Parse(s.Groups[1].Value) : 0);
    }

    private static DateOnly? ToDate(int schoolYear, int semester, int month, int day)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        if (schoolYear <= 0) schoolYear = DateTime.Today.Year;

        var year = (semester == 2 && month <= 2) ? schoolYear + 1 : schoolYear;
        return day > DateTime.DaysInMonth(year, month) ? null : new DateOnly(year, month, day);
    }

    private static double? FindLabel(IReadOnlyList<TextGlyph> line, string label)
    {
        var text = DocumentPage.TextOf(line);
        var at = text.IndexOf(label, StringComparison.Ordinal);
        if (at < 0) return null;

        var ordered = line.OrderBy(g => g.X).ToList();
        return at + label.Length <= ordered.Count
            ? ordered.Skip(at).Take(label.Length).Average(g => g.CenterX)
            : null;
    }

    private static double Mid(double a, double b) => (a + b) / 2;

    /// <summary>표 하나의 칸 경계.</summary>
    private readonly record struct TableLayout(
        double Top, double Bottom, double DateLeft,
        double DayX, double TopicLeft, double ContentLeft,
        double CountLeft, double KindLeft, double NoteLeft);
}
