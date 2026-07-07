namespace NeisAutoFill.Core.Models;

/// <summary>한 과목 시트: 영역 목록 + 학생 성적. (2단계 성적입력 엑셀 파싱 결과)</summary>
public sealed record SubjectSheet(
    string SubjectName,
    IReadOnlyList<string> Areas,
    IReadOnlyList<Student> Students);
