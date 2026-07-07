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

    public static void EnsureRoot() => Directory.CreateDirectory(Root);
}
