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

    /// <summary>콤보를 열고 target 텍스트 옵션을 클릭. 성공/사유 반환.</summary>
    public async Task<PickResult> OpenAndPickAsync(ILocator combo, string target)
    {
        await combo.ClickAsync();   // ★ 네이티브 클릭만 콤보를 연다

        // 옵션 팝업 폴링 (§4.3): cl-global-aside 안에 표시되는 옵션이 나타날 때까지
        var options = page.Locator(NeisSelectors.OptionItem);
        var deadline = DateTime.UtcNow + Timings.PopupPollTimeout;
        int count = 0;
        while (DateTime.UtcNow < deadline)
        {
            count = await options.CountAsync();
            if (count > 0 && await AnyVisibleAsync(options, count)) break;
            await Task.Delay(Timings.PopupPollStep);
        }
        if (count == 0)
            return new PickResult(false, "팝업 안 열림");

        for (int i = 0; i < count; i++)
        {
            var opt = options.Nth(i);
            if (!await opt.IsVisibleAsync()) continue;
            var text = Clean(await opt.InnerTextAsync());
            if (text == target)
            {
                await opt.ClickAsync();
                await Task.Delay(Timings.AfterOptionClick);
                return new PickResult(true, "");
            }
        }

        // 못 찾으면 ESC 로 팝업 닫기
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(Timings.PopupPollStep);
        return new PickResult(false, $"옵션 '{target}' 없음");
    }

    /// <summary>콤보를 열어 선택 가능한 옵션 목록을 읽고 다시 닫는다 (선택은 하지 않음). 전과목 과목 매핑용.</summary>
    public async Task<IReadOnlyList<string>> OpenAndReadOptionsAsync(ILocator combo)
    {
        await combo.ClickAsync();
        var options = page.Locator(NeisSelectors.OptionItem);
        var deadline = DateTime.UtcNow + Timings.PopupPollTimeout;
        int count = 0;
        while (DateTime.UtcNow < deadline)
        {
            count = await options.CountAsync();
            if (count > 0 && await AnyVisibleAsync(options, count)) break;
            await Task.Delay(Timings.PopupPollStep);
        }
        var list = await ReadOpenOptionsAsync();
        await page.Keyboard.PressAsync("Escape");   // 읽기만 — 다시 닫는다
        await Task.Delay(Timings.PopupPollStep);
        return list;
    }

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
