namespace NeisAutoFill.Core.Models;

/// <summary>화면 행 ↔ 엑셀 매칭이 끝난, 실제 입력할 한 건. §4.1 todo 항목.</summary>
public sealed record GradeTask(
    int RowIndex,
    string No,
    string Name,
    string Area,
    string TargetGrade);
