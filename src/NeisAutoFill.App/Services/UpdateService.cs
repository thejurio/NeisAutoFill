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

            // 에셋: zip 본체 + sha256 체크섬(있으면 무결성 검증에 사용)
            string? zipUrl = null, shaUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                var url = asset.GetProperty("browser_download_url").GetString();
                if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = url;
                else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zipUrl = url;
            }
            if (zipUrl is null) return;

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            var ok = await Application.Current.Dispatcher.InvokeAsync(() =>
                UpdatePromptWindow.Ask(latest.ToString(3), CurrentVersion.ToString(3), notes,
                    Application.Current.MainWindow));
            if (!ok) return;   // '나중에' — 다음 실행 때 다시 안내한다 (영구 건너뛰기 없음)

            await DownloadAndRestartAsync(zipUrl, shaUrl, latest);
        }
        catch (Exception)
        {
            // 업데이트 확인 실패는 조용히 무시 — 앱 사용에 영향 없음
        }
    }

    private async Task DownloadAndRestartAsync(string zipUrl, string? shaUrl, Version latest)
    {
        // 진행 창 — 사용자가 업데이트에 동의한 뒤이므로 화면에 명시적으로 보여준다
        UpdateWindow? win = null;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            win = new UpdateWindow(latest.ToString(3));
            win.Show();
        });
        void Report(string status, double? percent) =>
            Application.Current.Dispatcher.Invoke(() => win!.Report(status, percent));

        try
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "NeisAutoFill_Update");
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true);
            Directory.CreateDirectory(tempRoot);

            var zipPath = Path.Combine(tempRoot, "update.zip");
            var extractDir = Path.Combine(tempRoot, "files");

            // 스트리밍 다운로드 — 진행률 % 표시
            Report("다운로드 중... 0%", 0);
            using (var response = await Http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                var buffer = new byte[81920];
                long done = 0; int read; int lastPct = -1;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    if (total is { } t)
                    {
                        int pct = (int)(done * 100 / t);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            Report($"다운로드 중... {pct}% ({done / 1048576.0:F1}/{t / 1048576.0:F1} MB)", pct);
                        }
                    }
                    else Report($"다운로드 중... {done / 1048576.0:F1} MB", null);
                }
            }

            // 무결성 검증 — 체크섬이 있으면 다운로드 파일 해시와 대조 (손상·변조 방지)
            if (shaUrl is not null)
            {
                Report("무결성 확인 중...", null);
                var expected = await FetchExpectedHashAsync(shaUrl);
                if (expected is not null)
                {
                    var actual = await Task.Run(() => Sha256Hex(zipPath));
                    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "다운로드한 파일의 무결성 검증에 실패했습니다(체크섬 불일치). " +
                            "네트워크 문제일 수 있습니다. 잠시 후 다시 시도해 주세요.");
                }
            }

            Report("압축 푸는 중...", null);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extractDir));
            await ApplyAndRestartAsync(tempRoot, extractDir, Report);
        }
        catch (Exception ex)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                win!.Close();
                MessageBox.Show(
                    $"업데이트에 실패했습니다: {ex.Message}\n프로그램은 현재 버전으로 계속 사용할 수 있습니다.",
                    "업데이트 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }

    /// <summary>sha256 에셋을 받아 기대 해시(hex)만 뽑는다. 형식: "&lt;hash&gt;  파일명". 실패 시 null(검증 생략).</summary>
    private static async Task<string?> FetchExpectedHashAsync(string shaUrl)
    {
        try
        {
            var text = (await Http.GetStringAsync(shaUrl)).Trim();
            var first = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "";
            return first.Length == 64 && first.All(Uri.IsHexDigit) ? first : null;
        }
        catch { return null; }   // 체크섬을 못 받으면 검증을 생략하고 진행 (기존 동작 유지)
    }

    private static string Sha256Hex(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static async Task ApplyAndRestartAsync(string tempRoot, string extractDir, Action<string, double?> report)
    {
        report("적용 준비 중... 곧 재시작됩니다", null);

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
