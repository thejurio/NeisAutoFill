namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 셀 하나의 실행 분류 (기술설계 §5 실행 모델·§12).
/// 실패를 뭉뚱그리지 않는다 — 사용자가 무엇을 해야 하는지 분류마다 다르다.
/// </summary>
public enum AssignmentStatus
{
    /// <summary>입력 예정 — 대상이 확정됐고 셀이 비어 있다.</summary>
    Pending,
    /// <summary>이미 목표와 같다 — 건드리지 않는다(멱등성).</summary>
    AlreadyMatches,
    /// <summary>사용자가 "입력 안 함"으로 정했다.</summary>
    Skipped,
    /// <summary>원본에서 수업을 읽지 못했다.</summary>
    SourceUnresolved,
    /// <summary>매핑이 아직 정해지지 않았다.</summary>
    MappingUnresolved,
    /// <summary>같은 우선순위 규칙이 갈린다 — 사람이 정해야 한다.</summary>
    MappingConflict,
    /// <summary>창체인데 자율/동아리/진로가 정해지지 않았다(D-008).</summary>
    CreativeUnresolved,
    /// <summary>매핑 대상이 현재 카탈로그에 없다 — 교사가 목록에서 빠진 경우.</summary>
    OptionNotFound,
    /// <summary>기존 값이 목표와 다르다 — 승인 없이는 덮어쓰지 않는다(D-010).</summary>
    ExistingValueConflict,
    /// <summary>휴업일 등으로 셀을 쓸 수 없다.</summary>
    CellUnavailable,
    /// <summary>나이스의 이 학기에 없는 날짜다 — 다른 학년도·학기 문서를 넣은 경우.</summary>
    OutOfRange,
    /// <summary>같은 셀을 두 원본이 노린다.</summary>
    DuplicateTarget,
}

/// <summary>
/// 셀 하나에 대해 "무엇을 할지"가 확정된 한 줄 (기술설계 §5 TimetableAssignment).
/// 미리보기·실행·결과 대시보드가 모두 이 한 줄을 공유한다.
/// </summary>
/// <param name="Cell">날짜+교시</param>
/// <param name="SourceToken">원본 표기</param>
/// <param name="Status">실행 분류</param>
/// <param name="TargetStableKey">확정된 대상 (없으면 빈 문자열)</param>
/// <param name="CurrentStableKey">나이스의 현재 값 (비어 있으면 빈 문자열)</param>
/// <param name="Reason">이 분류가 나온 이유 — 사용자에게 그대로 보여 준다</param>
public sealed record TimetableAssignment(
    TimetableCell Cell,
    string SourceToken,
    AssignmentStatus Status,
    string TargetStableKey = "",
    string CurrentStableKey = "",
    string Reason = "")
{
    /// <summary>실제로 클릭이 일어날 항목인가.</summary>
    public bool WillWrite => Status == AssignmentStatus.Pending;

    /// <summary>사람이 풀어야 실행할 수 있는 상태인가 (기술설계 §12 사전 검증).</summary>
    public bool IsBlocking => Status is
        AssignmentStatus.OutOfRange or
        AssignmentStatus.SourceUnresolved or
        AssignmentStatus.MappingUnresolved or
        AssignmentStatus.MappingConflict or
        AssignmentStatus.CreativeUnresolved or
        AssignmentStatus.OptionNotFound or
        AssignmentStatus.ExistingValueConflict or
        AssignmentStatus.DuplicateTarget;
}

/// <summary>
/// 연간 실행 계획 전체 (기술설계 §12).
/// <b>막힌 항목이 하나라도 있으면 기본 실행을 잠근다</b> — 사용자가 명시적으로 범위를 좁혀야만 진행한다.
/// </summary>
public sealed record TimetablePlan(IReadOnlyList<TimetableAssignment> Assignments)
{
    public IReadOnlyList<TimetableAssignment> Blocking =>
        Assignments.Where(a => a.IsBlocking).ToList();

    public IReadOnlyList<TimetableAssignment> Writable =>
        Assignments.Where(a => a.WillWrite).ToList();

    /// <summary>기본 실행 가능 여부. 막힌 항목이 없어야 한다.</summary>
    public bool CanRun => Blocking.Count == 0 && Writable.Count > 0;

    /// <summary>분류별 건수 — 미리보기 요약에 쓴다.</summary>
    public IReadOnlyDictionary<AssignmentStatus, int> CountByStatus =>
        Assignments.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>주 단위 실행·체크포인트를 위해 주별로 나눈다(기술설계 §12).</summary>
    public IReadOnlyList<IGrouping<DateOnly, TimetableAssignment>> ByWeek =>
        Assignments.GroupBy(a => a.Cell.WeekStart).OrderBy(g => g.Key).ToList();
}
