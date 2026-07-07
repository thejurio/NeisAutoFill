using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>
/// 가상 스크롤 그리드 제어 (§3.6). 프록시 스크롤바 scrollTop 설정과
/// trusted 휠 이벤트(§8.4 ② — Playwright Mouse.Wheel) 두 방식을 제공.
/// 나이스 화면에는 숨겨진 그리드가 여럿 있으므로, scope(대상 그리드)를 주면
/// 그 안에서만 스크롤바/밴드를 찾는다 — 페이지 전체 첫 매치는 엉뚱한 요소일 수 있음.
/// </summary>
public sealed class GridScroller(IPage page)
{
    /// <summary>프록시 세로 스크롤바 요소와 (scrollHeight, clientHeight) 반환. 없으면 null.</summary>
    public async Task<(ILocator bar, double scrollHeight, double clientHeight)?> GetVScrollAsync(ILocator? scope = null)
    {
        var bars = scope is not null
            ? scope.Locator(NeisSelectors.VScroll)
            : page.Locator(NeisSelectors.VScroll);
        int n = await bars.CountAsync();
        for (int i = 0; i < n; i++)
        {
            var bar = bars.Nth(i);
            var metrics = await bar.EvaluateAsync<double[]>(
                "el => [el.scrollHeight, el.clientHeight]");
            if (metrics[0] > metrics[1])
                return (bar, metrics[0], metrics[1]);
        }
        return null;
    }

    /// <summary>프록시 스크롤바 scrollTop 설정 + scroll 이벤트 dispatch.</summary>
    public async Task ScrollProxyAsync(ILocator bar, double top)
    {
        await bar.EvaluateAsync(
            "(el, top) => { el.scrollTop = top; " +
            "el.dispatchEvent(new Event('scroll', {bubbles:true})); }", top);
    }

    // NOTE: trusted 휠(§8.4 ②)·키보드 복구는 화면 요동으로 UX 를 해쳐 제거했다 (사용자 결정).
    // 마지막 행 미렌더링(§8)은 원인 확정 전까지 리포트로만 알린다.
}
