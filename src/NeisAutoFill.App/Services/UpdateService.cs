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

    // 어셈블리 버전은 4자리(1.6.4.0)지만 저장·태그 비교는 3자리("1.6.4")를 쓴다.
    // 3자리로 정규화하지 않으면 저장값 재파싱 시 Revision=-1 이 되어 1.6.4.0 > 1.6.4.-1 로
    // 매 실행 '업데이트됨'으로 오판 → 패치창이 매번 뜬다. Major.Minor.Build 로 맞춘다.
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>업데이트 직후 1회 — 저장된 마지막 실행 버전보다 현재가 높으면
    /// 현재 버전 릴리스 노트(패치로그)를 보여주고 버전을 기록한다.</summary>
    public async Task ShowWhatsNewIfUpdatedAsync()
    {
        var current = CurrentVersion;
        var lastStr = settings.Options.LastRunVersion?.Trim() ?? "";

        // 첫 실행(기록 없음)은 패치로그 대신 조용히 버전만 기록 — 온보딩 화면이 안내를 맡는다
        if (string.IsNullOrEmpty(lastStr) || !Version.TryParse(lastStr, out var last))
        {
            SaveLastRunVersion(current);
            return;
        }
        if (current <= last) return;   // 업데이트 아님

        // 이전 실행 버전 < 태그 ≤ 현재 버전인 릴리스 노트를 전부 모은다 —
        // 옛 버전에서 여러 단계를 건너뛰어 업데이트해도 그 사이 변경점이 다 보이게 (최신부터).
        // 자동 생성 링크("Full Changelog")뿐인 노트는 스킵해 알맹이만 남긴다.
        string notes = "";
        var repo = settings.Options.UpdateRepo?.Trim();
        if (!string.IsNullOrEmpty(repo) && repo.Contains('/'))
        {
            try
            {
                var json = await Http.GetStringAsync(
                    $"https://api.github.com/repos/{repo}/releases?per_page=50");
                using var doc = JsonDocument.Parse(json);
                var sb = new System.Text.StringBuilder();
                foreach (var rel in doc.RootElement.EnumerateArray())   // API 기본 = 최신순
                {
                    var tag = rel.GetProperty("tag_name").GetString() ?? "";
                    if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) continue;
                    if (v <= last || v > current) continue;

                    var body = (rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "").Trim();
                    if (body.Length == 0 || body.StartsWith("**Full Changelog**")) continue;   // 링크뿐 → 스킵

                    var date = rel.TryGetProperty("published_at", out var pa) &&
                               DateTime.TryParse(pa.GetString(), out var dt)
                        ? $"  ·  {dt.ToLocalTime():yyyy-MM-dd}" : "";
                    sb.AppendLine($"# 🔖 v{v.ToString(3)}{date}");
                    sb.AppendLine(body);
                    sb.AppendLine();
                }
                notes = sb.ToString().Trim();
            }
            catch { /* 오프라인 등 — 노트 없이 진행 */ }
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
            UpdatePromptWindow.ShowWhatsNew(current.ToString(3), last.ToString(3), notes, Application.Current.MainWindow));
        SaveLastRunVersion(current);   // 확인 후 기록 — 다음 실행부턴 안 뜸
    }

    private void SaveLastRunVersion(Version v)
    {
        settings.Options = settings.Options with { LastRunVersion = v.ToString(3) };
        settings.Save();
    }

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

            var ok = await Application.Current.Dispatcher.InvokeAsync(() =>
                UpdatePromptWindow.Ask(latest.ToString(3), CurrentVersion.ToString(3),
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

        // 안전장치: 소스가 비어 있으면 /MIR 가 설치 폴더를 통째로 지울 수 있다 → 실행 파일이 있는지 확인
        if (!Directory.EnumerateFiles(srcDir, "*.exe", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("업데이트 압축 내용이 올바르지 않습니다 (실행 파일 없음). 적용을 중단했습니다.");

        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var exePath = Environment.ProcessPath ?? Path.Combine(appDir, "NeisAutoFill.App.exe");
        var pid = Environment.ProcessId;

        // 앱 종료 대기 → 파일 교체 → 재시작.
        // PowerShell 스크립트로 작성 — 경로에 %,&,^ 등 특수문자가 있어도 안전(cmd 의 % 확장 문제 회피, P8).
        // robocopy /MIR 로 미러링해 옛 버전에서 삭제된 파일도 정리(P7). 경로는 파일에 그대로 쓰되 '는 '' 로 이스케이프.
        var script = Path.Combine(tempRoot, "apply_update.ps1");
        await File.WriteAllTextAsync(script, $$"""
            $ErrorActionPreference = 'SilentlyContinue'
            $pid_ = {{pid}}
            while (Get-Process -Id $pid_ -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
            $src = '{{PsLit(srcDir)}}'
            $dst = '{{PsLit(appDir)}}'
            # /MIR = 미러(대상에만 있는 옛 파일 삭제). 로그/재시도 최소화.
            robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /R:3 /W:1 | Out-Null
            Start-Process -FilePath '{{PsLit(exePath)}}'
            """, System.Text.Encoding.UTF8);

        Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        });

        await Application.Current.Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
    }

    /// <summary>PowerShell 작은따옴표 리터럴 안에 넣기 위한 이스케이프 (' → '').</summary>
    private static string PsLit(string s) => s.Replace("'", "''");
}
