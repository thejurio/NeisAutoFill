using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace NeisAutoFill.App.Services;

/// <summary>
/// GitHub Releases 기반 자동업데이트 — 교체 엔진은 <b>Velopack</b>(v1.6.7~).
/// 확인·다운로드·검증·교체·재시작을 Velopack 이 맡고, 이 클래스는 "물어보고 진행률을 보여주는" UI 역할만 한다.
/// 델타 패키지를 지원해 바뀐 파일만 내려받는다(이전엔 매번 100MB 전체 zip).
///
/// 자작 업데이터(robocopy 스크립트)를 걷어낸 이유: 교체 실패를 조용히 삼키고 옛 버전으로 재시작해
/// "업데이트 안내 → 받아도 그대로 → 또 안내" 무한 루프가 났다(~v1.6.6).
///
/// 설정(settings.json)의 UpdateRepo("owner/repo")가 비어 있으면 아무것도 하지 않는다.
/// Velopack 으로 설치된 경우에만 동작 — 포터블·개발 실행에서는 조용히 건너뛴다.
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

    /// <summary>백그라운드 확인 — 새 버전이 있으면 사용자에게 묻고 업데이트 진행.
    /// 실제 다운로드·검증·교체·재시작은 Velopack 이 수행한다.</summary>
    public async Task CheckAndPromptAsync()
    {
        var repo = settings.Options.UpdateRepo?.Trim();
        if (string.IsNullOrEmpty(repo) || !repo.Contains('/')) return;

        try
        {
            var mgr = new UpdateManager(new GithubSource($"https://github.com/{repo}", null, prerelease: false));

            // Velopack 으로 설치된 경우에만 자동 업데이트 — 포터블 압축 해제본·개발 빌드는 대상 아님
            if (!mgr.IsInstalled) return;

            var info = await mgr.CheckForUpdatesAsync();
            if (info is null) return;   // 최신

            var latest = info.TargetFullRelease.Version.ToString();
            var ok = await Application.Current.Dispatcher.InvokeAsync(() =>
                UpdatePromptWindow.Ask(latest, CurrentVersion.ToString(3), Application.Current.MainWindow));
            if (!ok) return;   // '나중에' — 다음 실행 때 다시 안내한다 (영구 건너뛰기 없음)

            await DownloadAndRestartAsync(mgr, info, latest);
        }
        catch (Exception ex)
        {
            // 확인 단계 실패(오프라인 등)는 조용히 무시 — 앱 사용에 영향 없음
            Diag.Swallow(ex, "업데이트 확인");
        }
    }

    /// <summary>진행 창을 띄우고 Velopack 으로 내려받아 적용·재시작. 실패는 반드시 사용자에게 알린다.</summary>
    private static async Task DownloadAndRestartAsync(UpdateManager mgr, UpdateInfo info, string latest)
    {
        UpdateWindow? win = null;
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            win = new UpdateWindow(latest);
            win.Show();
        });
        void Report(string status, double? percent) =>
            Application.Current.Dispatcher.Invoke(() => win!.Report(status, percent));

        try
        {
            // 델타가 있으면 Velopack 이 알아서 델타만 받는다 (전체 재다운로드 아님)
            Report("다운로드 중... 0%", 0);
            await mgr.DownloadUpdatesAsync(info, p => Report($"다운로드 중... {p}%", p));

            Report("적용 준비 중... 곧 재시작됩니다", null);
            mgr.ApplyUpdatesAndRestart(info);   // 교체 후 앱 재시작 — 여기서 프로세스가 끝난다
        }
        catch (Exception ex)
        {
            // 조용히 넘기지 않는다 — 옛 업데이터가 실패를 삼켜 무한 루프를 만들었다
            Diag.Swallow(ex, "업데이트 적용");
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                win!.Close();
                MessageBox.Show(
                    $"업데이트에 실패했습니다: {ex.Message}\n\n" +
                    "프로그램은 현재 버전으로 계속 사용할 수 있습니다. " +
                    "계속 실패하면 GitHub 릴리스에서 최신 설치 파일을 직접 받아 설치해 주세요.",
                    "업데이트 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }
}
