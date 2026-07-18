using System.IO;
using NeisAutoFill.Core;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 앱 설정·데이터 파일 경로. %AppData%\NeisAutoFill\ 하위.
/// 다중 학급 프로필: 자료(엑셀)·서술문·최근파일은 <see cref="CurrentProfile"/> 별로 분리되고,
/// 설정·척도·프로필 목록은 공용(프로필 무관)이다. 기본 프로필은 기존 경로를 써서 하위호환.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NeisAutoFill");

    private static readonly string WorkspaceRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NeisAutoFill");

    /// <summary>현재 활성 프로필. 앱 시작 시 ProfileStore 가 설정(기본은 "기본"). 전환은 재시작으로 반영.</summary>
    public static string CurrentProfile { get; set; } = ProfilePaths.Default;

    // 공용 (프로필 무관) — 교육청·척도·글자크기 등은 학교/사용자 단위
    public static string ScalesJson => Path.Combine(Root, "scales.json");
    public static string SettingsJson => Path.Combine(Root, "settings.json");
    public static string ProfilesJson => Path.Combine(Root, "profiles.json");

    // 프로필별 — 자료·서술문·최근파일은 학급마다 다름
    public static string NarrativesJson => ProfilePaths.DataFile(Root, CurrentProfile, "narratives.json");
    public static string StateJson => ProfilePaths.DataFile(Root, CurrentProfile, "state.json");

    /// <summary>사용자 자료 기본 저장 위치 — 현재 프로필의 엑셀 폴더 (기본은 문서\NeisAutoFill).</summary>
    public static string Workspace => ProfilePaths.WorkspaceDir(WorkspaceRoot, CurrentProfile);

    public static void EnsureRoot() => Directory.CreateDirectory(Root);

    public static string EnsureWorkspace()
    {
        Directory.CreateDirectory(Workspace);
        return Workspace;
    }

    /// <summary>문서\NeisAutoFill 최상위 루트 (프로필·전담 폴더의 부모). 전담 명단·계획은 이 아래 전담\ 에 둔다.</summary>
    public static string EnsureWorkspaceRoot()
    {
        Directory.CreateDirectory(WorkspaceRoot);
        return WorkspaceRoot;
    }
}
