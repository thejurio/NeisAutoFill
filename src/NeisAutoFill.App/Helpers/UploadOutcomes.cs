using System.Windows;
using NeisAutoFill.Automation;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// 나이스 입력 결과(Outcome) 조립과 단건 대시보드 표시를 한 곳으로 (R7).
/// 성적·서술문 × 담임·전담 네 경로가 같은 문구·같은 창을 쓰도록 보장한다 —
/// 문구를 바꿀 땐 여기 한 곳만.
/// </summary>
internal static class UploadOutcomes
{
    /// <summary>단건 입력 결과 → Outcome. 카운트는 표시 단위(명)로 이미 환산해서 받는다
    /// (성적은 (학생×영역) 건을 명으로 distinct, 서술문은 그대로 명).</summary>
    public static BatchUploadRunner.SubjectOutcome Single(
        string label, int doneN, int skipN, int failN,
        IReadOnlyList<SkipItem> failedItems, string emptyMessage)
    {
        var status = failN > 0 ? BatchUploadRunner.SubjectStatus.Failed
                   : doneN == 0 ? BatchUploadRunner.SubjectStatus.Skipped
                   : BatchUploadRunner.SubjectStatus.Success;
        var msg = status switch
        {
            BatchUploadRunner.SubjectStatus.Failed => $"입력 실패 {failN}명 — 저장하지 않았습니다",
            BatchUploadRunner.SubjectStatus.Skipped => emptyMessage,
            _ => $"{doneN}명 입력 (저장은 나이스에서 직접)" + (skipN > 0 ? $" · 건너뜀 {skipN}명" : ""),
        };
        return new BatchUploadRunner.SubjectOutcome(label, status, doneN, skipN, failedItems, msg);
    }

    /// <summary>사용자 중지(취소) Outcome.</summary>
    public static BatchUploadRunner.SubjectOutcome Cancelled(string label) =>
        new(label, BatchUploadRunner.SubjectStatus.Cancelled, 0, 0,
            Array.Empty<SkipItem>(), "사용자 중지 — 저장 안 함");

    /// <summary>단건 결과 대시보드 — 배치와 동일한 창. 재시도는 같은 창을 새 결과로 갱신(창 중첩 없음).</summary>
    public static void ShowSingle(
        BatchUploadRunner.SubjectOutcome outcome,
        Func<Task<BatchUploadRunner.SubjectOutcome?>> retry)
    {
        BatchResultWindow.ShowResult(new[] { outcome }, "명",
            retry: async _ => new[] { await retry() ?? outcome },
            owner: Application.Current.MainWindow);
    }
}
