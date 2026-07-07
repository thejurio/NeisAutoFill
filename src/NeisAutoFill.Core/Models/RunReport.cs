namespace NeisAutoFill.Core.Models;

/// <summary>건너뛴/실패한 항목의 사유 포함 기록.</summary>
public sealed record SkipItem(string No, string Name, string Area, string Reason);

/// <summary>한 과목 실행 결과 리포트. §4.1 종료 리포트.</summary>
public sealed record RunReport(
    IReadOnlyList<GradeTask> Done,
    IReadOnlyList<SkipItem> Skipped,
    IReadOnlyList<SkipItem> Failed,
    IReadOnlyList<int> Missing);
