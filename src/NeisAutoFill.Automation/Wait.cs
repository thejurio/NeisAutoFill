namespace NeisAutoFill.Automation;

/// <summary>
/// <b>고정 시간을 기다리지 말고, 일어난 것을 보고 넘어간다.</b>
///
/// 이 프로그램이 여러 번 되풀이해 배운 원칙이다 (사용자 지시 2026-08-22):
/// 시간표 셀 입력에서 "클릭 성공은 입력 성공이 아니다" 로 시작해,
/// 평가계획에서 단계 카드·알림 상자·그리드 확정까지 <b>같은 실수를 세 번 더</b> 겪었다.
///
/// <b>지금보다 나빠질 수 없게</b> 만든다: <paramref name="limit"/> 는 예전에 무조건 기다리던 그 시간이다.
/// 일이 먼저 끝나면 그 순간 넘어가고(대개 훨씬 빠르다), 끝내 안 되면 예전만큼 기다린 뒤 false 다.
/// </summary>
public static class Wait
{
    /// <summary>다시 볼 때까지의 간격. 너무 촘촘하면 브라우저에 말 거는 값이 더 든다.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromMilliseconds(35);

    /// <summary>조건이 참이 될 때까지 본다. 참이 됐으면 true, 시간이 다하면 false.</summary>
    public static async Task<bool> UntilAsync(
        Func<Task<bool>> done, TimeSpan limit, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + limit;

        while (true)
        {
            if (await done()) return true;
            if (DateTime.UtcNow >= deadline) return false;

            await Task.Delay(Step, ct);
        }
    }

    /// <summary>
    /// 값이 나올 때까지 본다. 나왔으면 그 값, 시간이 다하면 null.
    /// <c>UntilAsync</c> 와 달리 <b>찾은 것을 그대로 돌려준다</b> — 두 번 찾지 않아도 된다.
    /// </summary>
    public static async Task<T?> ForAsync<T>(
        Func<Task<T?>> find, TimeSpan limit, CancellationToken ct = default) where T : class
    {
        var deadline = DateTime.UtcNow + limit;

        while (true)
        {
            if (await find() is { } found) return found;
            if (DateTime.UtcNow >= deadline) return null;

            await Task.Delay(Step, ct);
        }
    }

    /// <summary>
    /// 값이 <b>두 번 연달아 같게</b> 읽힐 때까지 본다 — 화면이 그려지는 중이면 값이 흔들린다.
    /// 나이스 알림은 제목·단추가 먼저 뜨고 본문이 나중에 채워져, 뜨자마자 읽으면 반쪽만 잡힌다.
    /// </summary>
    public static async Task<T?> SettledAsync<T>(
        Func<Task<T?>> read, TimeSpan limit, CancellationToken ct = default) where T : class
    {
        var deadline = DateTime.UtcNow + limit;
        T? last = null;

        while (true)
        {
            var now = await read();
            if (now is not null && Equals(now, last)) return now;

            if (DateTime.UtcNow >= deadline) return now ?? last;

            last = now;
            await Task.Delay(Step, ct);
        }
    }
}
