using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Automation;

/// <summary>
/// 전과목 자동 업로드 공통 루프 (등급·서술문 공용, A안).
/// "과목 전환 → 실행 → 검증 → 통과 시 [저장] → 다음 과목 / 실패·취소 시 그 자리에서 중단"
/// 을 한 벌로 유지한다 — 저장 정책이 바뀌면 여기 한 곳만 고친다.
/// 실행 내용(등급/서술문)은 runSubject 콜백으로 주입.
/// 결과는 과목별 <see cref="SubjectOutcome"/> 로 반환해 대시보드 표시·재시도에 쓴다.
/// </summary>
public static class BatchUploadRunner
{
    public enum SubjectStatus { Success, Skipped, Failed, SwitchFailed, SaveFailed, Cancelled, NotReached }

    /// <summary>한 과목 실행 결과 (runSubject 콜백 반환). Failed 는 실패 학생 상세.</summary>
    public sealed record SubjectResult(int Done, IReadOnlyList<SkipItem> Failed, int Skipped, bool UserCancelled)
    {
        public int FailedCount => Failed.Count;
    }

    /// <summary>대시보드용 과목별 최종 결과.</summary>
    public sealed record SubjectOutcome(
        string Subject, SubjectStatus Status, int Done, int Skipped,
        IReadOnlyList<SkipItem> FailedItems, string Message)
    {
        public int FailedCount => FailedItems.Count;
        /// <summary>재시도 대상인가 (성공·생략이 아닌 모든 상태).</summary>
        public bool NeedsRetry => Status is SubjectStatus.Failed or SubjectStatus.SwitchFailed
            or SubjectStatus.SaveFailed or SubjectStatus.Cancelled or SubjectStatus.NotReached;
    }

    /// <param name="runSubject">과목 하나 입력 실행 (전환 완료 후 호출됨)</param>
    /// <param name="unit">요약 문구 단위 (등급="건", 서술문="명")</param>
    public static async Task<List<SubjectOutcome>> RunAsync(
        IReadOnlyList<string> subjects,
        INeisEngine engine,
        Func<string, Task<SubjectResult>> runSubject,
        Action<string> log,
        string unit,
        CancellationToken ct)
    {
        var outcomes = new List<SubjectOutcome>();
        try
        {
            for (int i = 0; i < subjects.Count; i++)
            {
                var subject = subjects[i];
                log(new string('─', 50));
                log($"[전과목 {i + 1}/{subjects.Count}] '{subject}' 과목으로 전환 중...");

                var (selOk, selWhy) = await engine.SelectSubjectAsync(subject, ct);
                if (!selOk)
                {
                    outcomes.Add(new SubjectOutcome(subject, SubjectStatus.SwitchFailed, 0, 0,
                        Array.Empty<SkipItem>(), $"과목 전환 실패 — {selWhy}"));
                    break;   // 화면 상태를 모르는 채 계속 가지 않는다
                }

                var r = await runSubject(subject);

                if (r.UserCancelled)
                {
                    outcomes.Add(new SubjectOutcome(subject, SubjectStatus.Cancelled, r.Done, r.Skipped,
                        r.Failed, "사용자 취소 — 저장 안 함"));
                    break;
                }
                if (r.FailedCount > 0)
                {
                    outcomes.Add(new SubjectOutcome(subject, SubjectStatus.Failed, r.Done, r.Skipped,
                        r.Failed, $"입력 실패 {r.FailedCount}{unit} — 저장하지 않고 중단"));
                    log($"⚠ '{subject}' 검증 실패로 저장하지 않았습니다. 나이스에서 값을 확인하세요.");
                    break;
                }
                if (r.Done == 0)
                {
                    outcomes.Add(new SubjectOutcome(subject, SubjectStatus.Skipped, 0, r.Skipped,
                        Array.Empty<SkipItem>(), "입력할 값 없음 — 저장 생략"));
                    continue;
                }

                log($"[{subject}] 검증 통과 ({r.Done}{unit}) → 저장 중...");
                var (saveOk, saveWhy) = await engine.SaveScreenAsync(ct);
                if (!saveOk)
                {
                    outcomes.Add(new SubjectOutcome(subject, SubjectStatus.SaveFailed, r.Done, r.Skipped,
                        Array.Empty<SkipItem>(), $"입력 {r.Done}{unit} 완료했으나 저장 실패({saveWhy}) — 중단"));
                    break;
                }
                outcomes.Add(new SubjectOutcome(subject, SubjectStatus.Success, r.Done, r.Skipped,
                    Array.Empty<SkipItem>(),
                    $"{r.Done}{unit} 입력·저장" + (r.Skipped > 0 ? $" (건너뜀 {r.Skipped})" : "")));
            }
        }
        catch (OperationCanceledException) { /* 남은 과목은 아래에서 미도달 처리 */ }
        catch (Exception ex) { log($"전과목 입력 오류: {ex.Message}"); }

        // break·취소·예외 이후 실행되지 않은 나머지 과목은 '미도달'
        for (int i = outcomes.Count; i < subjects.Count; i++)
            outcomes.Add(new SubjectOutcome(subjects[i], SubjectStatus.NotReached, 0, 0,
                Array.Empty<SkipItem>(), "앞 과목 중단으로 실행되지 않음"));

        return outcomes;
    }

    /// <summary>재시도할 과목 (실패·중단·미도달·취소). 원래 순서 유지.</summary>
    public static IReadOnlyList<string> RetrySubjects(IReadOnlyList<SubjectOutcome> outcomes) =>
        outcomes.Where(o => o.NeedsRetry).Select(o => o.Subject).ToList();

    /// <summary>로그용 요약 줄 (기존 ✓/✗/· 포맷 유지).</summary>
    public static List<string> Summarize(IReadOnlyList<SubjectOutcome> outcomes)
    {
        var lines = new List<string>();
        foreach (var o in outcomes)
        {
            var mark = o.Status switch
            {
                SubjectStatus.Success => "✓",
                SubjectStatus.Skipped or SubjectStatus.NotReached => "·",
                _ => "✗",
            };
            lines.Add($"{mark} {o.Subject}: {o.Message}");
        }
        return lines;
    }
}
