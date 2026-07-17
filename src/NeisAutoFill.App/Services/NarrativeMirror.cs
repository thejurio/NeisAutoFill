using System.IO;
using NeisAutoFill.Core;
using NeisAutoFill.Excel;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 서술문을 작업공간 서술문.xlsx 로 저장 ([💾 저장] 버튼에서 호출).
/// 사용자가 엑셀로 직접 열어 보거나 수정할 수 있는 사본 — 원본은 narratives.json (내부 자동 저장).
/// </summary>
public sealed class NarrativeMirror
{
    private readonly NarrativeStore _store;

    public event Action<string>? Log;

    public NarrativeMirror(NarrativeStore store)
    {
        _store = store;
    }

    public static string MirrorPath => Path.Combine(AppPaths.Workspace, "서술문.xlsx");

    /// <summary>지금 저장. 성공 시 true. 실패 사유는 Log 이벤트로.</summary>
    public bool SaveNow()
    {
        var all = _store.All();
        if (all.Count == 0)
        {
            Log?.Invoke("저장할 서술문이 없습니다.");
            return false;
        }

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
            Log?.Invoke($"서술문 저장: {MirrorPath}");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"⚠ 서술문.xlsx 저장 실패 ({ex.Message}) — 파일이 열려 있으면 닫아 주세요.");
            return false;
        }
    }
}
