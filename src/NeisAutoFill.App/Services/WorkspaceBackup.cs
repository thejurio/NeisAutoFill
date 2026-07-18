using System.IO;
using NeisAutoFill.Core;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 작업공간 백업 — 명단·계획·성적·서술문과 설정을 한 zip 으로 묶는다.
/// 데이터 유실(재설치·PC 교체·실수 삭제) 대비. 복원은 zip 을 풀어 각 위치에 되돌리면 된다.
/// 실제 zip 생성은 Core/BackupArchive (테스트됨).
/// </summary>
public static class WorkspaceBackup
{
    public static string SuggestFileName(DateTime now) => BackupArchive.SuggestFileName(now);

    /// <summary>백업 대상 (zip 안 경로 → 실제 파일). 존재 여부는 CreateZip 이 거른다.</summary>
    public static IReadOnlyList<(string Entry, string Path)> CollectFiles() => new List<(string, string)>
    {
        ("data/scales.json", AppPaths.ScalesJson),
        ("data/settings.json", AppPaths.SettingsJson),
        ("data/narratives.json", AppPaths.NarrativesJson),
        ("data/state.json", AppPaths.StateJson),
        ("workspace/성적.xlsx", Path.Combine(AppPaths.Workspace, "성적.xlsx")),
        ("workspace/평가계획서.xlsx", Path.Combine(AppPaths.Workspace, "평가계획서.xlsx")),
        ("workspace/서술문.xlsx", Path.Combine(AppPaths.Workspace, "서술문.xlsx")),
    };

    /// <summary>현재 작업공간을 zip 으로 백업.</summary>
    public static (bool Ok, string Error, int Count) Create(string destZip) =>
        BackupArchive.CreateZip(destZip, CollectFiles());
}
