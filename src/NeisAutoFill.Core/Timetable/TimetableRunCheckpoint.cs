using System.Security.Cryptography;
using System.Text;

namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 연간 입력을 어디까지 끝냈는지 기록한다 (기술설계 §12, 로드맵 T8).
///
/// <b>이 기록은 권위가 아니다.</b> 재개할 때 최종 판단은 언제나 나이스의 현재 값으로 한다 —
/// 체크포인트는 "이미 끝난 주를 다시 훑지 않게" 해 주는 것뿐이고,
/// 사람이 나이스에서 직접 지웠을 수도 있기 때문에 값 자체를 믿지 않는다.
///
/// 완료로 기록하는 조건은 하나다: <b>저장까지 하고 다시 조회해서 값이 맞았을 때만</b>.
/// </summary>
/// <param name="Scope">어느 학교·학년도·학기·반의 작업인지</param>
/// <param name="PlanFingerprint">그때 실행하던 계획의 지문 — 원본 문서가 바뀌면 달라진다</param>
/// <param name="CatalogFingerprint">그때 나이스 과목·교사 목록의 지문</param>
/// <param name="CompletedWeeks">저장·재조회까지 끝난 주의 시작일(월요일)</param>
/// <param name="UpdatedAt">마지막 갱신 시각</param>
public sealed record TimetableRunCheckpoint(
    TimetableProfileScope Scope,
    string PlanFingerprint,
    string CatalogFingerprint,
    IReadOnlyList<DateOnly> CompletedWeeks,
    DateTimeOffset UpdatedAt)
{
    public static TimetableRunCheckpoint Start(
        TimetableProfileScope scope, string planFingerprint, string catalogFingerprint, DateTimeOffset now)
        => new(scope, planFingerprint, catalogFingerprint, Array.Empty<DateOnly>(), now);

    public bool IsCompleted(DateOnly weekStart) => CompletedWeeks.Contains(weekStart);

    /// <summary>한 주를 완료로 기록한 새 체크포인트. 같은 주를 두 번 넣지 않는다.</summary>
    public TimetableRunCheckpoint WithWeekDone(DateOnly weekStart, DateTimeOffset now) =>
        IsCompleted(weekStart)
            ? this with { UpdatedAt = now }
            : this with
            {
                CompletedWeeks = CompletedWeeks.Append(weekStart).OrderBy(d => d).ToList(),
                UpdatedAt = now,
            };

    /// <summary>
    /// 이 체크포인트를 이어서 써도 되는가.
    /// 원본 문서나 나이스 목록이 바뀌었으면 이어서 하면 안 된다 —
    /// 끝냈다고 적힌 주가 지금 계획과 다른 내용일 수 있다.
    /// </summary>
    public bool CanResume(TimetableProfileScope scope, string planFingerprint, string catalogFingerprint) =>
        Scope == scope && PlanFingerprint == planFingerprint && CatalogFingerprint == catalogFingerprint;

    /// <summary>이어서 못 쓰는 이유. 이어서 써도 되면 null.</summary>
    public string? ResumeBlocker(TimetableProfileScope scope, string planFingerprint, string catalogFingerprint)
    {
        if (Scope != scope) return "다른 학급·학기의 기록입니다.";
        if (PlanFingerprint != planFingerprint) return "원본 문서나 매핑이 바뀌었습니다.";
        if (CatalogFingerprint != catalogFingerprint) return "나이스의 과목·교사 목록이 바뀌었습니다.";
        return null;
    }

    public string Describe() =>
        CompletedWeeks.Count == 0
            ? "완료된 주 없음"
            : $"{CompletedWeeks.Count}개 주 완료 (마지막 {CompletedWeeks[^1]:MM-dd} 주)";

    /// <summary>
    /// 정렬에 독립적인 계획 지문. 셀·원본표기·대상이 하나라도 달라지면 값이 바뀐다.
    /// 분류(Status)는 넣지 않는다 — 화면 상태에 따라 달라지므로 같은 계획이어도 매번 변한다.
    /// </summary>
    public static string FingerprintOf(IEnumerable<TimetableAssignment> assignments)
    {
        var joined = string.Join("\n", assignments
            .Select(a => $"{a.Cell.Date:yyyy-MM-dd}|{a.Cell.Period}|{a.SourceToken}|{a.TargetStableKey}")
            .OrderBy(s => s, StringComparer.Ordinal));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }
}
