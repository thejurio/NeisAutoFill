namespace NeisAutoFill.Core.Models;

/// <summary>나이스에 입력할 학생 한 명의 교과 서술문 (AI 생성기 결과가 원천).</summary>
public sealed record NarrativeEntry(string No, string Name, string Text);

/// <summary>서술문 입력 실행 결과.</summary>
public sealed record NarrativeReport(
    IReadOnlyList<NarrativeEntry> Done,
    IReadOnlyList<SkipItem> Skipped,
    IReadOnlyList<SkipItem> Failed);
