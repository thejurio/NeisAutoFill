using System.IO;
using System.IO.Compression;

namespace NeisAutoFill.Core;

/// <summary>백업 zip 생성 (순수 파일 로직). 어떤 파일을 넣을지는 호출 측이 정한다.</summary>
public static class BackupArchive
{
    /// <summary>백업 파일 기본 이름 (예: NeisAutoFill_백업_20260718_1530.zip).</summary>
    public static string SuggestFileName(DateTime now) =>
        $"NeisAutoFill_백업_{now:yyyyMMdd_HHmm}.zip";

    /// <summary>백업 zip 안의 엔트리 경로를 복원 대상 실제 경로로 변환. dataRoot=%AppData%\NeisAutoFill, workspace=문서\NeisAutoFill.
    /// 우리 백업 형식(data/… 또는 workspace/…)이 아니거나 상위경로(..) 가 섞이면 null(무시).</summary>
    public static string? ResolveRestorePath(string entry, string dataRoot, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(entry) || entry.Contains("..")) return null;
        var e = entry.Replace('\\', '/');
        if (e.StartsWith("data/") && e.Length > 5)
            return System.IO.Path.Combine(dataRoot, e["data/".Length..]);
        if (e.StartsWith("workspace/") && e.Length > 10)
            return System.IO.Path.Combine(workspaceRoot, e["workspace/".Length..]);
        return null;
    }

    /// <summary>zip 이 우리 백업 형식인지(복원 가능한 엔트리가 하나라도 있는지) 검사.</summary>
    public static bool LooksLikeBackup(IEnumerable<string> entries) =>
        entries.Any(e => { var x = e.Replace('\\', '/'); return x.StartsWith("data/") || x.StartsWith("workspace/"); });

    /// <summary>(zip 안 경로 → 실제 파일) 목록을 zip 으로 묶는다. 존재하지 않는 파일은 건너뛴다.</summary>
    public static (bool Ok, string Error, int Count) CreateZip(
        string destZip, IReadOnlyList<(string Entry, string Path)> files)
    {
        var present = files.Where(f => File.Exists(f.Path)).ToList();
        if (present.Count == 0)
            return (false, "백업할 자료가 없습니다. 명단·성적·서술문을 먼저 저장해 주세요.", 0);
        try
        {
            var dir = Path.GetDirectoryName(destZip);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(destZip)) File.Delete(destZip);

            using var zip = ZipFile.Open(destZip, ZipArchiveMode.Create);
            foreach (var (entry, path) in present)
                zip.CreateEntryFromFile(path, entry);
            return (true, "", present.Count);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }
    }
}
