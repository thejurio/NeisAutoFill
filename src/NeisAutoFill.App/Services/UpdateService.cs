using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace NeisAutoFill.App.Services;

/// <summary>
/// GitHub Releases 기반 자동업데이트.
/// 시작 시 최신 릴리스 태그(v1.2.3)를 현재 버전과 비교 → 새 버전이면 사용자 확인 후
/// zip 에셋을 내려받아 임시 폴더에 풀고, 앱 종료 후 파일을 교체·재시작하는 cmd 스크립트 실행.
/// 설정(settings.json)의 UpdateRepo("owner/repo")가 비어 있으면 아무것도 하지 않는다.
/// </summary>
public sealed class UpdateService(GeneratorSettingsStore settings)
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NeisAutoFill-Updater");   // GitHub API 필수
        return c;
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>백그라운드 확인 — 새 버전이 있으면 사용자에게 묻고 업데이트 진행.</summary>
    public async Task CheckAndPromptAsync()
    {
        var repo = settings.Options.UpdateRepo?.Trim();
        if (string.IsNullOrEmpty(repo) || !repo.Contains('/')) return;

        try
        {
            var json = await Http.GetStringAsync(
                $"https://api.github.com/repos/{repo}/releases/latest");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return;
            if (latest <= CurrentVersion) return;

            // zip 에셋 URL
            string? zipUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (zipUrl is null) return;

            var ok = await Application.Current.Dispatcher.InvokeAsync(() =>
                MessageBox.Show(
                    $"새 버전 v{latest} 이 있습니다 (현재 v{CurrentVersion}).\n지금 업데이트할까요?\n" +
                    "(다운로드 후 프로그램이 자동으로 재시작됩니다)",
                    "업데이트", MessageBoxButton.YesNo, MessageBoxImage.Information)
                == MessageBoxResult.Yes);
            if (!ok) return;

            await DownloadAndRestartAsync(zipUrl);
        }
        catch (Exception)
        {
            // 업데이트 확인 실패는 조용히 무시 — 앱 사용에 영향 없음
        }
    }

    private async Task DownloadAndRestartAsync(string zipUrl)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "NeisAutoFill_Update");
        if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
        Directory.CreateDirectory(tempRoot);

        var zipPath = Path.Combine(tempRoot, "update.zip");
        var extractDir = Path.Combine(tempRoot, "files");

        var bytes = await Http.GetByteArrayAsync(zipUrl);
        await File.WriteAllBytesAsync(zipPath, bytes);
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // zip 안에 단일 최상위 폴더가 있으면 그 내부를 소스로
        var dirs = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);
        var srcDir = (dirs.Length == 1 && files.Length == 0) ? dirs[0] : extractDir;

        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exePath = Environment.ProcessPath ?? Path.Combine(appDir, "NeisAutoFill.App.exe");
        var pid = Environment.ProcessId;

        // 앱 종료 대기 → 파일 교체 → 재시작
        var script = Path.Combine(tempRoot, "apply_update.cmd");
        await File.WriteAllTextAsync(script, $"""
            @echo off
            :wait
            tasklist /fi "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            xcopy /e /y /q "{srcDir}\*" "{appDir}\" >nul
            start "" "{exePath}"
            """);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
    }
}
