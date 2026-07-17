using Microsoft.Playwright;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Automation;

/// <summary>
/// 행 지도 작성 (§4.2). 프록시 스크롤로 그리드를 훑으며 렌더된 행의
/// aria-label 을 수집해 {rowindex → (번호,성명,영역)} 을 만든다. 입력은 하지 않는다.
/// 스캔은 패스당 1회 JS evaluate 로 현재 렌더된 행 전체를 한 번에 읽어 stale 을 회피.
/// </summary>
public sealed class RowMapBuilder(IPage page, GridScroller scroller)
{
    // 그리드에 현재 렌더된 data-rowindex 행들의 [idx, 번호셀 aria, 성명셀 aria, 영역셀 aria]
    private const string ScanScript = @"
() => {
  const rows = document.querySelectorAll('div.cl-grid[role=""grid""][aria-colcount=""8""] div.cl-grid-row[data-rowindex]');
  const out = [];
  rows.forEach(r => {
    if (r.offsetParent === null) return; // 화면에 없는 행 제외
    const idx = parseInt(r.getAttribute('data-rowindex'));
    const cell = ci => {
      const c = r.querySelector('div[role=""gridcell""][data-cellindex=""' + ci + '""]');
      return c ? (c.getAttribute('aria-label') || '') : '';
    };
    out.push([idx, cell('1'), cell('2'), cell('3')]);
  });
  return out;
}";

    // 진단: 현재 렌더된 data-rowindex 목록 (누락 원인 파악용)
    private const string RenderedIdxScript = @"
() => [...document.querySelectorAll(
  'div.cl-grid[role=""grid""][aria-colcount=""8""] div.cl-grid-row[data-rowindex]')]
  .filter(r => r.offsetParent !== null)
  .map(r => r.getAttribute('data-rowindex')).join(',')";

    public async Task<IReadOnlyDictionary<int, RowMeta>> BuildAsync(
        ILocator grid,
        int expected,
        Action<string> log,
        CancellationToken ct)
    {
        var map = new Dictionary<int, RowMeta>();
        var rawArea = new Dictionary<int, string>();   // 진단: 영역 파싱 실패 행의 원본 라벨

        async Task ScanAsync()
        {
            var rows = await page.EvaluateAsync<string[][]>(ScanScript);
            foreach (var row in rows)
            {
                if (!int.TryParse(row[0], out var idx) || map.ContainsKey(idx)) continue;
                var meta = Parse(row[1], row[2], row[3]);
                if (meta.Area is not null) map[idx] = meta;   // 영역 파싱된 행만 (오버레이 배제)
                else if (!string.IsNullOrWhiteSpace(row[3])) rawArea[idx] = row[3];
            }
        }

        var vscroll = await scroller.GetVScrollAsync(grid);

        if (vscroll is { } vs) { await scroller.ScrollProxyAsync(vs.bar, 0); await Task.Delay(Timings.AfterScroll, ct); }
        await ScanAsync();

        if (vscroll is { } v)
        {
            double pos = 0, step = Math.Max(v.clientHeight * 0.55, 80);
            while (pos < v.scrollHeight)
            {
                ct.ThrowIfCancellationRequested();
                pos += step;
                await scroller.ScrollProxyAsync(v.bar, Math.Min(pos, v.scrollHeight));
                await Task.Delay(Timings.AfterScroll, ct);
                await ScanAsync();
            }
        }
        else if (map.Count < expected)
        {
            // 프록시 스크롤바를 못 찾는 화면 (2026-07-17 진단에서 확인) —
            // CLX 공식 reveal 로 렌더 창(약 10행)을 단계적으로 이동하며 전체를 훑는다 (화면 요동 없음).
            log("  프록시 스크롤바 없음 → CLX reveal 스윕으로 행을 훑습니다.");
            for (int idx = 0; idx < expected; idx += 8)
            {
                ct.ThrowIfCancellationRequested();
                await ClxGridApi.RevealAsync(page, expected, Math.Min(idx + 7, expected - 1));
                await Task.Delay(Timings.AfterScroll, ct);
                await ScanAsync();
                if (map.Count >= expected) break;
            }
        }

        // §8 마지막 행: 프록시 스크롤 최대치에서 마지막 행이 누락될 수 있다.
        // CLX 공식 API reveal(rowIndex) 로 복구 (2026-07-07 실기기 탐사로 확정, 화면 요동 없음).
        var missing = Missing(map, expected);
        if (missing.Count > 0)
        {
            foreach (var idx in missing.Take(10))
            {
                ct.ThrowIfCancellationRequested();
                var res = await ClxGridApi.RevealAsync(page, expected, idx);
                log($"  누락 행 {idx} → CLX reveal ({res})");
                await Task.Delay(Timings.AfterScroll, ct);
                await ScanAsync();
            }
            missing = Missing(map, expected);
            if (missing.Count > 0)
                log($"  진단(최종 렌더 행): {await page.EvaluateAsync<string>(RenderedIdxScript)}");
        }

        if (vscroll is { } vend) { await scroller.ScrollProxyAsync(vend.bar, 0); await Task.Delay(Timings.AfterScroll, ct); }

        // 진단: 화면 행 지도 전체 (rowindex → 번호/성명/영역). 엑셀 영역 순서와 비교용.
        foreach (var idx in map.Keys.OrderBy(k => k))
        {
            var m = map[idx];
            log($"    행{idx}: {m.No}번 {m.Name} 영역='{m.Area}'");
        }
        // 영역 파싱 실패 행(오버레이 아님)의 원본 라벨 — 이름 있는데 영역만 못 읽은 경우
        foreach (var idx in rawArea.Keys.OrderBy(k => k))
            log($"    ⚠ 행{idx} 영역 파싱 실패 원본='{rawArea[idx]}'");

        return map;
    }

    private static List<int> Missing(Dictionary<int, RowMeta> map, int expected) =>
        Enumerable.Range(0, expected).Where(i => !map.ContainsKey(i)).ToList();

    internal static RowMeta Parse(string noLabel, string nameLabel, string areaLabel)
    {
        var no = Match(NeisSelectors.NoRegex(), noLabel);
        var name = Match(NeisSelectors.NameRegex(), nameLabel);
        var area = Match(NeisSelectors.AreaRegex(), areaLabel);
        return new RowMeta(no, name, area);
    }

    private static string? Match(System.Text.RegularExpressions.Regex re, string label)
    {
        var m = re.Match((label ?? string.Empty).Trim());
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }
}
