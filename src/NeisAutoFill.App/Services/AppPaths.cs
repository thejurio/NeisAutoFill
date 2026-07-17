using System.IO;

namespace NeisAutoFill.App.Services;

/// <summary>앱 설정·데이터 파일 경로. %AppData%\NeisAutoFill\ 하위.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeisAutoFill");

    public static string ScalesJson => Path.Combine(Root, "scales.json");
    public static string SettingsJson => Path.Combine(Root, "settings.json");
    public static string NarrativesJson => Path.Combine(Root, "narratives.json");
    public static string StateJson => Path.Combine(Root, "state.json");

    /// <summary>사용자 자료 기본 저장 위치 (문서\NeisAutoFill) — 양식·성적 등 엑셀 파일용.</summary>
    public static string Workspace { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeisAutoFill");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);

    public static string EnsureWorkspace()
    {
        Directory.CreateDirectory(Workspace);
        return Workspace;
    }
}
