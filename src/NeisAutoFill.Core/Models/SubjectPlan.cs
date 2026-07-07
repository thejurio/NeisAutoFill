namespace NeisAutoFill.Core.Models;

/// <summary>평가기준 한 항목: 기준 서술문 + 관련 성취기준 코드.</summary>
public sealed record CriteriaEntry(string Text, string? Achievement);

/// <summary>
/// 1단계 평가계획서 한 과목의 파싱 결과 (Index.html cachedSubjectsData 와 동형).
/// Criteria 키 = (영역명, 등급라벨).
/// </summary>
public sealed record SubjectPlan(
    string SubjectName,
    IReadOnlyList<string> Domains,
    IReadOnlyDictionary<(string Domain, string Grade), CriteriaEntry> Criteria);
