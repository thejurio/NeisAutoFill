using System.IO;
using System.Windows.Threading;
using NeisAutoFill.Core;
using NeisAutoFill.Excel;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 서술문 저장소가 바뀔 때마다 (디바운스 3초) 작업공간 서술문.xlsx 로 자동 미러.
/// 사용자가 엑셀로 직접 열어 보거나 수정할 수 있는 사본 — 원본은 narratives.json.
/// 파일 잠금 등 실패는 조용히 넘기고 다음 변경 때 재시도.
/// </summary>
public sealed class NarrativeMirror
{
    private readonly NarrativeStore _store;
    private readonly DispatcherTimer _timer;

    public event Action<string>? Log;

    public NarrativeMirror(NarrativeStore store)
    {
        _store = store;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => { _timer.Stop(); WriteMirror(); };
        _store.Changed += () => { _timer.Stop(); _timer.Start(); };
    }

    public static string MirrorPath => Path.Combine(AppPaths.Workspace, "서술문.xlsx");

    private void WriteMirror()
    {
        var all = _store.All();
        if (all.Count == 0) return;   // 전부 삭제된 경우 파일은 남겨둠 (사용자 사본 보호)

        var bySubject = all
            .GroupBy(e => e.Subject)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(string, string, string)>)g
                    .Select(e => (e.No, e.Name, e.Text))
                    .OrderBy(t => int.TryParse(t.No, out var n) ? n : int.MaxValue)
                    .ToList());
        try
        {
            AppPaths.EnsureWorkspace();
            NarrativeWorkbookWriter.Write(MirrorPath, bySubject);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"⚠ 서술문.xlsx 자동 저장 실패 ({ex.Message}) — 파일이 열려 있으면 닫아 주세요.");
        }
    }
}
