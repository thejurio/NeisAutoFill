namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 사용자가 고른 입력 범위와 방식 (로드맵 T8).
///
/// 연간 전체를 한 번에 넣을 필요는 없다 — 학기 초에 한 달치만 넣고 나중에 나머지처럼
/// 나눠서 진행할 수 있어야 한다.
/// </summary>
/// <param name="From">시작일(포함)</param>
/// <param name="To">종료일(포함)</param>
/// <param name="AllowOverwrite">
/// 이미 값이 있는 칸도 덮어쓸지 (D-010).
/// 나이스 학급시간표는 기준시간표에서 이미 채워져 있는 경우가 흔해,
/// 끄면 대부분의 칸에서 멈추게 된다.
/// </param>
public sealed record TimetableRangeChoice(DateOnly From, DateOnly To, bool AllowOverwrite)
{
    public bool Contains(DateOnly date) => date >= From && date <= To;

    public IReadOnlyList<TimetableSourceLesson> Filter(IEnumerable<TimetableSourceLesson> lessons) =>
        lessons.Where(l => Contains(l.Cell.Date)).ToList();
}
