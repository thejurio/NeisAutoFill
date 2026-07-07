namespace NeisAutoFill.Core.Models;

/// <summary>엑셀에서 읽은 한 학생의 과목 성적. Grades: 영역명 → 등급 라벨.</summary>
public sealed record Student(
    string No,
    string Name,
    IReadOnlyDictionary<string, string> Grades,
    string? SpecialNote = null);
