namespace NeisAutoFill.Core.Models;

/// <summary>평가기준 한 항목: 기준 서술문 + 관련 성취기준 + 평가요소.</summary>
/// <param name="Text">그 등급의 평가기준 서술문</param>
/// <param name="Achievement">성취기준 (문장 전체. 없으면 null)</param>
/// <param name="Element">
/// 평가요소. 나이스 [평가계획(안)관리]가 성취기준과 <b>따로</b> 요구하는 칸이라 여기서 들고 다닌다
/// (없으면 null — 예전 파일에는 이 칸이 없었다).
/// </param>
public sealed record CriteriaEntry(string Text, string? Achievement, string? Element = null);

/// <summary>
/// 1단계 평가계획서 한 과목의 파싱 결과 (Index.html cachedSubjectsData 와 동형).
/// Criteria 키 = (영역명, 등급라벨).
/// </summary>
public sealed record SubjectPlan(
    string SubjectName,
    IReadOnlyList<string> Domains,
    IReadOnlyDictionary<(string Domain, string Grade), CriteriaEntry> Criteria);
