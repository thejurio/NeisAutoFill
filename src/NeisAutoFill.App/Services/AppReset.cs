using System.Diagnostics;
using System.IO;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 프로그램을 처음 실행 상태로 초기화. 설정·상태·서술문·척도 등 내부 데이터를 지우고,
/// 선택 시 작업공간 엑셀(성적·평가계획서·서술문)까지 지운다. 실행 후 재시작이 필요하다.
/// 되돌릴 수 없으므로 호출 측이 반드시 사용자 확인을 받는다.
/// </summary>
public static class AppReset
{
    /// <param name="alsoWorkspaceFiles">작업공간(문서\NeisAutoFill)의 성적·계획·서술문 엑셀도 삭제할지.</param>
    public static void ResetAndRestart(bool alsoWorkspaceFiles)
    {
        // 내부 상태 파일 (설정 포함 — 교육청·척도·톤 등 전부 기본값으로)
        foreach (var file in new[]
        {
            AppPaths.SettingsJson, AppPaths.StateJson,
            AppPaths.NarrativesJson, AppPaths.ScalesJson,
            Path.Combine(AppPaths.Root, "diag.txt"),
        })
            TryDelete(file);

        if (alsoWorkspaceFiles && Directory.Exists(AppPaths.Workspace))
            foreach (var name in new[] { "성적.xlsx", "평가계획서.xlsx", "서술문.xlsx" })
                TryDelete(Path.Combine(AppPaths.Workspace, name));

        // 지금 프로세스를 종료하고 새로 시작 — 지워진 상태로 깨끗하게 부팅
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            // 잠깐 기다렸다 재시작하는 별도 프로세스 (현재 앱이 종료된 뒤 실행)
            var cmd = $"/c timeout /t 1 /nobreak >nul & start \"\" \"{exe}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
        }
        System.Windows.Application.Current.Shutdown();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 잠긴 파일은 재시작 후에도 남을 수 있음 */ }
    }
}
