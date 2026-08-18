namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 시간표 셀 하나의 주소 = <b>실제 날짜 + 교시</b> (D-003).
/// 주차 번호나 화면 열 순서로 셀을 식별하지 않는다 — 주차는 탐색용일 뿐이다.
/// </summary>
public readonly record struct TimetableCell(DateOnly Date, int Period)
{
    public DayOfWeek DayOfWeek => Date.DayOfWeek;

    /// <summary>그 주의 월요일 — 주차 이동·체크포인트 단위로 쓴다(셀 주소로는 쓰지 않는다).</summary>
    public DateOnly WeekStart => Date.AddDays(-(((int)Date.DayOfWeek + 6) % 7));

    public override string ToString() => $"{Date:yyyy-MM-dd} {Period}교시";
}
