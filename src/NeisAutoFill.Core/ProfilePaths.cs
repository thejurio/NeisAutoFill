namespace NeisAutoFill.Core;

/// <summary>
/// 다중 학급 프로필의 경로·이름 규칙 (순수 로직). 프로필 = 학급/과목 단위 작업공간.
/// 기본 프로필("기본")은 기존 경로 그대로 써서 하위호환을 지킨다(기존 사용자 무영향).
/// </summary>
public static class ProfilePaths
{
    /// <summary>기본 프로필 이름 — 이 프로필은 하위폴더 없이 기존 경로를 쓴다.</summary>
    public const string Default = "기본";

    /// <summary>이 프로필이 기본(=기존 경로)인가.</summary>
    public static bool IsDefault(string? profile) =>
        string.IsNullOrWhiteSpace(profile) || profile == Default;

    /// <summary>프로필명이 폴더명으로 안전한지 (파일시스템 금지문자·예약어·길이).</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        name = name.Trim();
        if (name.Length > 40) return false;
        if (name == "." || name == "..") return false;
        // Windows 파일명 금지문자 + 경로 구분자
        foreach (var c in name)
            if (c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || c < ' ')
                return false;
        return true;
    }

    /// <summary>프로필의 자료(엑셀) 폴더. 기본은 workspaceRoot 그대로, 그 외는 하위폴더.</summary>
    public static string WorkspaceDir(string workspaceRoot, string? profile) =>
        IsDefault(profile) ? workspaceRoot : System.IO.Path.Combine(workspaceRoot, profile!.Trim());

    /// <summary>프로필의 내부 데이터 파일 경로 (narratives.json·state.json 등).
    /// 기본은 appRoot 직속, 그 외는 appRoot\profiles\{프로필}\ 아래.</summary>
    public static string DataFile(string appRoot, string? profile, string fileName) =>
        IsDefault(profile)
            ? System.IO.Path.Combine(appRoot, fileName)
            : System.IO.Path.Combine(appRoot, "profiles", profile!.Trim(), fileName);
}
