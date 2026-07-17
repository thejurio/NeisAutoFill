using System.IO;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 의도적으로 무시하는 예외의 진단 기록. 사용자에겐 완전 무음이지만
/// %AppData%\NeisAutoFill\diag.txt 에 남아 "원인 불명" 문제를 추적할 수 있다.
/// 같은 (맥락, 예외타입)은 세션당 1회만 기록해 파일이 불지 않는다.
/// </summary>
public static class Diag
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> SeenThisSession = new();
    private const long MaxBytes = 512 * 1024;   // 넘치면 새로 시작

    public static void Swallow(Exception ex, string context)
    {
        try
        {
            var key = context + "|" + ex.GetType().Name;
            lock (Gate)
            {
                if (!SeenThisSession.Add(key)) return;
                var path = Path.Combine(AppPaths.Root, "diag.txt");
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex.GetType().Name} — {ex.Message}{Environment.NewLine}");
            }
        }
        catch { /* 진단 기록 실패는 더 기록할 곳이 없다 */ }
    }
}
