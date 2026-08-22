using NeisAutoFill.Automation.Abstractions;

namespace NeisAutoFill.Automation;

/// <summary>
/// 평가계획을 쓰기 전에 나이스 교과를 목표 교과로 바꾸고, 실제 표시값까지 다시 확인한다.
/// 교과 전환 자체는 등급·서술문 배치에서 실기기 검증된 <see cref="INeisEngine.SelectSubjectAsync"/>를
/// 재사용하되, 평가계획은 잘못된 교과에 저장되면 되돌리기 어려워 한 번 더 읽어 대조한다.
/// </summary>
public static class EvalPlanSubjectSelector
{
    public sealed record Result(bool Ok, string Message, string? CurrentSubject = null);

    public static async Task<Result> SelectAndVerifyAsync(
        INeisEngine engine,
        string targetSubject,
        CancellationToken ct = default)
    {
        var target = targetSubject.Trim();
        if (target.Length == 0)
            return new(false, "입력할 교과 이름이 비어 있습니다.");

        var (selected, why) = await engine.SelectSubjectAsync(target, ct);
        if (!selected)
        {
            var detail = why == INeisEngine.SubjectNotInList
                ? $"나이스 교과 목록에 '{target}'이 없습니다."
                : $"나이스 교과를 '{target}'(으)로 바꾸지 못했습니다 — {why}";
            return new(false, detail);
        }

        var current = (await engine.GetCurrentSubjectAsync(ct))?.Trim();
        if (!string.Equals(current, target, StringComparison.Ordinal))
        {
            var shown = string.IsNullOrWhiteSpace(current) ? "읽지 못함" : current;
            return new(false,
                $"교과 전환 후 화면은 '{shown}'이지만 입력 대상은 '{target}'입니다. 입력하지 않고 멈췄습니다.",
                current);
        }

        return new(true, why, current);
    }
}
