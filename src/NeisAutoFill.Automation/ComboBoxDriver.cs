using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>
/// 커스텀 단계 콤보박스 조작 (§3.4·3.5). 열기는 반드시 네이티브 클릭 —
/// JS 클릭/키/마우스이벤트 dispatch 는 CLX 에서 동작 안 함(§3.7 금지 목록).
/// </summary>
public sealed class ComboBoxDriver(IPage page)
{
    public sealed record PickResult(bool Ok, string Reason);

    /// <summary>콤보의 현재 표시값. 빈 값은 NBSP 로 오므로 제거 후 trim.</summary>
    public static async Task<string> ReadValueAsync(ILocator combo)
    {
        var text = await combo.InnerTextAsync();
        return Clean(text);
    }

    /// <summary>콤보를 열고 target 텍스트 옵션을 클릭. 가상 스크롤 팝업이면 스크롤하며 찾는다. 성공/사유 반환.</summary>
    public async Task<PickResult> OpenAndPickAsync(ILocator combo, string target)
    {
        await combo.ClickAsync();   // ★ 네이티브 클릭만 콤보를 연다

        var options = page.Locator(NeisSelectors.OptionItem);
        if (!await WaitPopupAsync(options))
            return new PickResult(false, "팝업 안 열림");

        // 팝업을 위로 되감고, 보이는 옵션에서 찾기 → 없으면 아래로 스크롤하며 반복.
        // ★ 리셋 직후 가상 렌더가 위쪽 옵션을 다시 그릴 시간을 준다 (안 주면 위 옵션을 놓치고 아래로만 감).
        await ResetPopupScrollAsync();
        await Task.Delay(Timings.PopupPollStep);
        for (int step = 0; step < 40; step++)
        {
            int count = await options.CountAsync();
            for (int i = 0; i < count; i++)
            {
                var opt = options.Nth(i);
                if (!await opt.IsVisibleAsync()) continue;
                if (Clean(await opt.InnerTextAsync()) == target)
                {
                    await opt.ClickAsync();
                    await Task.Delay(Timings.AfterOptionClick);
                    return new PickResult(true, "");
                }
            }
            if (!await ScrollPopupAsync(200)) break;   // 더 못 내려가면 끝
            await Task.Delay(Timings.PopupPollStep);
        }

        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(Timings.PopupPollStep);
        return new PickResult(false, $"옵션 '{target}' 없음");
    }

    /// <summary>콤보를 열어 선택 가능한 옵션 목록을 스크롤하며 전부 읽고 다시 닫는다 (선택 안 함). 전과목 과목 매핑용.</summary>
    public async Task<IReadOnlyList<string>> OpenAndReadOptionsAsync(ILocator combo)
    {
        await combo.ClickAsync();
        var options = page.Locator(NeisSelectors.OptionItem);
        if (!await WaitPopupAsync(options))
        {
            await page.Keyboard.PressAsync("Escape");
            return System.Array.Empty<string>();
        }

        // 프록시 스크롤바 기반 가상 렌더 — 위에서부터 아래로 훑으며 새 옵션을 누적 (순서·중복제거)
        var order = new List<string>();
        var seen = new HashSet<string>();
        await ResetPopupScrollAsync();
        await Task.Delay(Timings.PopupPollStep);   // 리셋 후 위쪽 옵션 재렌더 대기 (안 주면 위 옵션 누락)
        for (int step = 0; step < 40; step++)
        {
            foreach (var t in await ReadOpenOptionsAsync())
                if (t.Length > 0 && seen.Add(t)) order.Add(t);
            if (!await ScrollPopupAsync(200)) break;   // 바닥이면 끝
            await Task.Delay(Timings.PopupPollStep);
        }
        // 바닥에서 한 번 더 읽어 마지막 항목 보장
        foreach (var t in await ReadOpenOptionsAsync())
            if (t.Length > 0 && seen.Add(t)) order.Add(t);

        await page.Keyboard.PressAsync("Escape");   // 읽기만 — 다시 닫는다
        await Task.Delay(Timings.PopupPollStep);
        return order;
    }

    /// <summary>옵션 팝업이 뜰 때까지 폴링. 떴으면 true.</summary>
    private async Task<bool> WaitPopupAsync(ILocator options)
    {
        var deadline = DateTime.UtcNow + Timings.PopupPollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            int count = await options.CountAsync();
            if (count > 0 && await AnyVisibleAsync(options, count)) return true;
            await Task.Delay(Timings.PopupPollStep);
        }
        return false;
    }

    /// <summary>옵션 팝업(cl-global-aside) 안의 스크롤 컨테이너를 맨 위로.</summary>
    private Task ResetPopupScrollAsync() => ScrollPopupToAsync(0);

    /// <summary>팝업 스크롤을 delta 만큼 아래로. 실제로 움직였으면 true, 바닥이면 false (그리드와 같은 프록시 방식).</summary>
    private Task<bool> ScrollPopupAsync(double delta) => page.EvaluateAsync<bool>(
        @"(delta) => {
            const aside = document.querySelector('.cl-global-aside');
            if (!aside) return false;
            const els = [aside, ...aside.querySelectorAll('*')];
            for (const el of els) {
                if (el.scrollHeight > el.clientHeight + 2) {
                    const before = el.scrollTop;
                    el.scrollTop = Math.min(el.scrollTop + delta, el.scrollHeight);
                    el.dispatchEvent(new Event('scroll', {bubbles:true}));
                    if (el.scrollTop !== before) return true;
                }
            }
            return false;
        }", delta);

    private Task ScrollPopupToAsync(double top) => page.EvaluateAsync(
        @"(top) => {
            const aside = document.querySelector('.cl-global-aside');
            if (!aside) return;
            const els = [aside, ...aside.querySelectorAll('*')];
            for (const el of els) {
                if (el.scrollHeight > el.clientHeight + 2) {
                    el.scrollTop = top;
                    el.dispatchEvent(new Event('scroll', {bubbles:true}));
                }
            }
        }", top);

    /// <summary>현재 열린(또는 이 콤보의) 팝업에서 선택 가능한 옵션 텍스트 목록.
    /// §9.2 동적 화이트리스트 — 나이스 실제 옵션과 교차검증용.</summary>
    public async Task<IReadOnlyList<string>> ReadOpenOptionsAsync()
    {
        var options = page.Locator(NeisSelectors.OptionItem);
        int count = await options.CountAsync();
        var list = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var opt = options.Nth(i);
            if (await opt.IsVisibleAsync())
                list.Add(Clean(await opt.InnerTextAsync()));
        }
        return list;
    }

    private static async Task<bool> AnyVisibleAsync(ILocator options, int count)
    {
        for (int i = 0; i < count; i++)
            if (await options.Nth(i).IsVisibleAsync()) return true;
        return false;
    }

    // 빈 값은 NBSP(U+00A0)로 온다(§3.4). NBSP 만 제거하고 trim.
    private static string Clean(string? s) =>
        (s ?? string.Empty).Replace(" ", string.Empty).Trim();
}
