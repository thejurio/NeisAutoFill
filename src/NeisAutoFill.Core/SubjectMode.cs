namespace NeisAutoFill.Core;

/// <summary>
/// 전담 모드 도메인 (F9 M1). 담임과 독립적인 데이터 계층.
/// 명단=(학년·반), 평가계획=(학년·과목), 작업=(학년·반·과목) 세 축으로 정규화된다.
/// </summary>

/// <summary>학년·반 (명단의 키). 예: 3학년 1반.</summary>
public readonly record struct ClassRef(int Grade, string Class)
{
    /// <summary>표시·폴더용 이름. 예: "3-1".</summary>
    public string Key => $"{Grade}-{Class}";
    public override string ToString() => Key;
}

/// <summary>전담이 담당하는 한 작업 단위 = (학년, 반, 과목). 명단 × 평가계획의 교차점.</summary>
public sealed record TeachingUnit(int Grade, string Class, string Subject)
{
    public ClassRef ClassRef => new(Grade, Class);
    /// <summary>사용자 표시·폴더용. 예: "3-1 영어".</summary>
    public string Display => $"{Grade}-{Class} {Subject}";
}

/// <summary>평가계획 파일에서 AI 가 발견한 (학년, 과목) 단위 (F9 M4b).
/// 학년 표기가 문서에 없으면 Grade=0 (불명) — 사용자가 선택 창에서 지정한다.</summary>
public sealed record PlanUnit(int Grade, string Subject)
{
    /// <summary>학년이 확정됐는지 (0 = 문서에 학년 표기 없어 사용자 지정 필요).</summary>
    public bool HasGrade => Grade is >= 1 and <= 6;
    /// <summary>표시용. 예: "3학년 영어" / 불명이면 "(학년?) 영어".</summary>
    public string Display => (HasGrade ? $"{Grade}학년" : "(학년?)") + $" {Subject}";
}

/// <summary>
/// 전담 모드의 파일 경로 규칙 (순수). 담임과 완전히 분리된 "전담\" 하위에 정규화 저장.
/// 명단은 반별 1개, 평가계획은 (학년·과목)별 1개 → 같은 학년·과목이면 여러 반이 공유.
/// </summary>
public static class SubjectModePaths
{
    /// <summary>전담 자료 루트 폴더명 (workspaceRoot 하위). 담임 파일과 섞이지 않는다.</summary>
    public const string RootFolder = "전담";

    /// <summary>학년·반·과목이 파일/폴더명으로 안전한지.</summary>
    public static bool IsValidSubject(string? subject) => ProfilePaths.IsValidName(subject);
    public static bool IsValidClass(string? cls) => ProfilePaths.IsValidName(cls);
    public static bool IsValidGrade(int grade) => grade is >= 1 and <= 6;

    /// <summary>반 명단 파일. 예: {ws}\전담\명단\3-1.xlsx</summary>
    public static string RosterFile(string workspaceRoot, ClassRef c) =>
        System.IO.Path.Combine(workspaceRoot, RootFolder, "명단", $"{c.Grade}-{c.Class}.xlsx");

    /// <summary>학년 평가계획 파일 — 담임 평가계획서와 같은 포맷(과목 시트들 + [학생명단]).
    /// 과목은 파일 안 시트로 분리 → PlanWorkbookLoader/Writer 그대로 재사용. 예: {ws}\전담\평가계획\3학년.xlsx</summary>
    public static string PlanFile(string workspaceRoot, int grade) =>
        System.IO.Path.Combine(workspaceRoot, RootFolder, "평가계획", $"{grade}학년.xlsx");

    /// <summary>명단 파일명("3-1")을 ClassRef 로 파싱. 형식이 아니면 null (RosterFile 과 왕복).</summary>
    public static ClassRef? ParseRosterName(string fileNameNoExt)
    {
        if (string.IsNullOrEmpty(fileNameNoExt)) return null;
        var dash = fileNameNoExt.IndexOf('-');
        if (dash <= 0 || dash == fileNameNoExt.Length - 1) return null;
        if (int.TryParse(fileNameNoExt[..dash], out var g) && IsValidGrade(g))
            return new ClassRef(g, fileNameNoExt[(dash + 1)..]);
        return null;
    }

    /// <summary>계획 파일명("3학년")을 학년으로 파싱. 형식이 아니면 null (PlanFile 과 왕복).</summary>
    public static int? ParsePlanName(string fileNameNoExt)
    {
        var digits = fileNameNoExt.Replace("학년", "");
        return int.TryParse(digits, out var g) && IsValidGrade(g) ? g : null;
    }

    /// <summary>작업(성적·서술문) 폴더. 예: {ws}\전담\작업\3-1_영어</summary>
    public static string UnitDir(string workspaceRoot, TeachingUnit u) =>
        System.IO.Path.Combine(workspaceRoot, RootFolder, "작업", $"{u.Grade}-{u.Class}_{u.Subject}");

    /// <summary>작업 폴더 안의 성적 파일.</summary>
    public static string UnitGradeFile(string workspaceRoot, TeachingUnit u) =>
        System.IO.Path.Combine(UnitDir(workspaceRoot, u), "성적.xlsx");

    /// <summary>작업 폴더 안의 서술문 파일.</summary>
    public static string UnitNarrativeFile(string workspaceRoot, TeachingUnit u) =>
        System.IO.Path.Combine(UnitDir(workspaceRoot, u), "서술문.xlsx");
}
