using System.IO;
using System.IO.Compression;
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

    /// <summary>백업 zip 을 원래 위치로 복원(덮어쓰기). data/→%AppData%, workspace/→문서\NeisAutoFill.
    /// 형식이 아닌 zip 은 거부. 성공 시 파일 수 반환 — 호출 측이 재시작해야 반영됨.</summary>
    public static (bool Ok, string Error, int Count) Restore(string zipPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            if (!BackupArchive.LooksLikeBackup(entries.Select(e => e.FullName)))
                return (false, "이 프로그램에서 만든 백업 파일이 아닙니다 (data/·workspace/ 폴더가 없습니다).", 0);

            AppPaths.EnsureRoot();
            AppPaths.EnsureWorkspace();
            int count = 0;
            foreach (var e in entries)
            {
                var dest = BackupArchive.ResolveRestorePath(e.FullName, AppPaths.Root, AppPaths.Workspace);
                if (dest is null) continue;   // 우리 폴더 밖·경로 탈출은 무시
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                e.ExtractToFile(dest, overwrite: true);
                count++;
            }
            return count > 0 ? (true, "", count) : (false, "복원할 파일이 없습니다.", 0);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }
}
