using NeisAutoFill.Automation.Abstractions;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 전과목 자동 업로드 공통 루프 (등급·서술문 공용, A안).
/// "과목 전환 → 실행 → 검증 → 통과 시 [저장] → 다음 과목 / 실패·취소 시 그 자리에서 중단"
/// 을 한 벌로 유지한다 — 저장 정책이 바뀌면 여기 한 곳만 고친다.
/// 실행 내용(등급/서술문)은 runSubject 콜백으로 주입.
/// </summary>
public static class BatchUploadRunner
{
    /// <summary>한 과목 실행 결과 — RunReport 를 공통 판정에 필요한 만큼만 요약.</summary>
    public sealed record SubjectResult(int Done, int Failed, int Skipped, bool UserCancelled);

    /// <param name="runSubject">과목 하나 입력 실행 (전환 완료 후 호출됨)</param>
    /// <param name="unit">요약 문구 단위 (등급="건", 서술문="명")</param>
    /// <returns>과목별 결과 요약 줄 목록 (✓/✗/· 접두)</returns>
    public static async Task<List<string>> RunAsync(
        IReadOnlyList<string> subjects,
        INeisEngine engine,
        Func<string, Task<SubjectResult>> runSubject,
        Action<string> log,
        string unit,
        CancellationToken ct)
    {
        var summary = new List<string>();
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
                    summary.Add($"✗ {subject}: 과목 전환 실패 — {selWhy}");
                    break;   // 화면 상태를 모르는 채 계속 가지 않는다
                }

                var r = await runSubject(subject);

                if (r.UserCancelled)
                {
                    summary.Add($"· {subject}: 사용자 취소 — 저장 안 함");
                    break;
                }
                if (r.Failed > 0)
                {
                    summary.Add($"✗ {subject}: 입력 실패 {r.Failed}{unit} — 저장하지 않고 중단");
                    log($"⚠ '{subject}' 검증 실패로 저장하지 않았습니다. 나이스에서 값을 확인하세요.");
                    break;
                }
                if (r.Done == 0)
                {
                    summary.Add($"· {subject}: 입력할 값 없음 — 저장 생략");
                    continue;
                }

                log($"[{subject}] 검증 통과 ({r.Done}{unit}) → 저장 중...");
                var (saveOk, saveWhy) = await engine.SaveScreenAsync(ct);
                if (!saveOk)
                {
                    summary.Add($"✗ {subject}: 입력 {r.Done}{unit} 완료했으나 저장 실패({saveWhy}) — 중단. " +
                                "나이스에서 직접 [저장]을 눌러주세요.");
                    break;
                }
                summary.Add($"✓ {subject}: {r.Done}{unit} 입력·저장" +
                            (r.Skipped > 0 ? $" (건너뜀 {r.Skipped})" : ""));
            }
        }
        catch (OperationCanceledException) { summary.Add("⛔ 사용자 중지"); }
        catch (Exception ex)
        {
            summary.Add($"✗ 오류: {ex.Message}");
            log($"전과목 입력 오류: {ex.Message}");
        }
        return summary;
    }
}
