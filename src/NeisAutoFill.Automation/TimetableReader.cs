using System.Text.Json;
using Microsoft.Playwright;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Automation;

/// <summary>주차 목록 한 줄 (grdWeek).</summary>
/// <param name="Index">그리드 행 번호 — 주차 전환에 쓴다</param>
/// <param name="Start">주 시작일</param>
/// <param name="End">주 종료일</param>
/// <param name="Name">"2주차 (2026.08.24.~2026.08.30.)"</param>
/// <param name="Event">그 주의 주요 행사 (휴업일 등)</param>
public sealed record TimetableWeek(int Index, DateOnly Start, DateOnly End, string Name, string Event);

/// <summary>
/// 학급시간표관리 화면을 <b>읽기만</b> 한다 (기술설계 §10, 실기기검증 §3·§4-A).
/// 수업 선택·저장은 이 클래스가 하지 않는다 — 쓰기는 별도 단계(T7)에서 승급한다.
///
/// 2026-08-18 실측으로 확정한 것:
/// <list type="bullet">
/// <item>DOM 에 grdWeekByClsByTi id 는 없다 → <b>구조</b>로 찾는다(셀 8개 + 첫 셀이 "N교시")</item>
/// <item>aria-colcount/rowcount 는 논리 수(8×8)와 다르다 → 세는 데 쓰지 않는다</item>
/// <item>메뉴 항목 id(uuid-*)는 매번 바뀐다 → 텍스트 구조와 안정 키만 쓴다(D-004)</item>
/// <item>메뉴는 반드시 [취소]로 닫는다 — 다른 항목을 누르면 데이터가 바뀐다</item>
/// </list>
/// </summary>
public sealed class TimetableReader(IPage page)
{
    /// <summary>시간표 그리드를 구조로 찾는다. 못 찾으면 -1.</summary>
    private const string FindGridJs = @"() => {
  const grids = [...document.querySelectorAll('div.cl-grid[role=grid]')];
  for (let i = 0; i < grids.length; i++) {
    const row = grids[i].querySelector('div.cl-grid-row[data-rowindex]');
    if (!row) continue;
    const cells = row.querySelectorAll('div[role=gridcell]');
    if (cells.length !== 8) continue;                                   // 교시 + 월~일
    if (!/^\d+\s*교시$/.test((cells[0].innerText || '').trim())) continue;
    return i;
  }
  return -1;
}";

    /// <summary>CLX 컨트롤에서 그리드를 id 로 찾아 데이터 행을 읽는다 (DOM 이 아니라 논리 데이터).</summary>
    private const string ReadRowsJs = @"(gridId) => {
  if (typeof cpr === 'undefined') return null;
  const seen = new Set(); let g = null;
  const visit = c => {
    if (!c || seen.has(c)) return; seen.add(c);
    try { if (c.type === 'grid' && c.id === gridId) g = c; } catch (e) {}
    try { if (c.getChildren) c.getChildren().forEach(visit); } catch (e) {}
    try { if (c.content && c.content.getChildren) c.content.getChildren().forEach(visit); } catch (e) {}
    try { if (c.getEmbeddedAppInstance) { const ea = c.getEmbeddedAppInstance(); if (ea && ea.getContainer) visit(ea.getContainer()); } } catch (e) {}
  };
  cpr.core.Platform.INSTANCE.getAllRunningAppInstances().forEach(a => { try { visit(a.getContainer()); } catch (e) {} });
  if (!g) return null;

  const rows = [];
  const n = g.getDataRowCount ? g.getDataRowCount() : 0;
  for (let i = 0; i < n; i++) {
    const o = {};
    try {
      const r = g.getDataRow(i);
      // 행 객체가 제공하는 표현을 순서대로 시도한다 — 순수 JSON 복제는 순환참조로 실패할 수 있다
      let src = null;
      try { if (typeof r.toObject === 'function') src = r.toObject(); } catch (e) {}
      if (!src) { try { if (typeof r.getRowData === 'function') src = r.getRowData(); } catch (e) {} }
      if (!src) { try { src = JSON.parse(JSON.stringify(r)); } catch (e) {} }
      if (src) for (const k of Object.keys(src)) {
        const v = src[k];
        if (v !== null && typeof v !== 'object' && typeof v !== 'function') o[k] = String(v);
      }
    } catch (e) {}
    rows.push(o);
  }
  return rows;
}";

    /// <summary>현재 화면이 학급시간표관리인지 — 시간표 그리드가 보이면 준비된 것으로 본다.</summary>
    public async Task<bool> IsTimetableScreenAsync() =>
        await page.EvaluateAsync<int>(FindGridJs) >= 0;

    /// <summary>주차 목록. 주차 전환·행사(휴업일) 확인에 쓴다.</summary>
    public async Task<IReadOnlyList<TimetableWeek>> ReadWeeksAsync()
    {
        var rows = await ReadClxRowsAsync("grdWeek");
        var list = new List<TimetableWeek>();

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (!TryYmd(r, "weekBgngYmd", out var start) || !TryYmd(r, "weekEndYmd", out var end)) continue;
            list.Add(new TimetableWeek(i, start, end,
                r.GetValueOrDefault("weekCntNm", ""), r.GetValueOrDefault("event", "")));
        }
        return list;
    }

    /// <summary>현재 보이는 주의 셀 값을 읽는다. 빈 셀은 빈 문자열로 들어온다.</summary>
    public async Task<TimetableGridSnapshot> ReadCurrentWeekAsync()
    {
        var rows = await ReadClxRowsAsync("grdWeekByClsByTi");
        return TimetableGridParser.Parse(rows);
    }

    /// <summary>주차 전환 — 주차 그리드의 행을 클릭한다(조회만, 저장 아님).</summary>
    public async Task SelectWeekAsync(int weekRowIndex)
    {
        var weekGrid = await page.EvaluateAsync<int>(@"() => {
          const grids = [...document.querySelectorAll('div.cl-grid[role=grid]')];
          for (let i = 0; i < grids.length; i++) {
            const row = grids[i].querySelector('div.cl-grid-row[data-rowindex]');
            const first = row && row.querySelector('div[role=gridcell]');
            if (first && /주차/.test(first.innerText || '')) return i;
          }
          return -1;
        }");
        if (weekGrid < 0) throw new InvalidOperationException("주차 목록 그리드를 찾지 못했습니다.");

        await page.Locator($"div.cl-grid[role=grid] >> nth={weekGrid} >> " +
                           $"div.cl-grid-row[data-rowindex='{weekRowIndex}'] div[role=gridcell] >> nth=0")
                  .ClickAsync();
        await page.WaitForTimeoutAsync((float)Timings.TimetableWeekChange.TotalMilliseconds);
    }

    /// <summary>
    /// 셀 하나를 우클릭해 할당 가능한 과목·교사 목록(카탈로그)을 읽는다.
    /// <b>반드시 [취소]로 닫는다.</b> 메뉴가 열리지 않으면 null — 학기 시작 전·휴업일의 정상 동작이다.
    /// </summary>
    public async Task<TimetableCatalog?> ReadCatalogAsync(int rowIndex, int dayColumn)
    {
        var gridIdx = await page.EvaluateAsync<int>(FindGridJs);
        if (gridIdx < 0) throw new InvalidOperationException("시간표 그리드를 찾지 못했습니다.");

        await page.Locator($"div.cl-grid[role=grid] >> nth={gridIdx} >> " +
                           $"div.cl-grid-row[data-rowindex='{rowIndex}'] div[role=gridcell] >> nth={dayColumn}")
                  .ClickAsync(new LocatorClickOptions { Button = MouseButton.Right });
        await page.WaitForTimeoutAsync((float)Timings.TimetableMenuOpen.TotalMilliseconds);

        // 열린 메뉴 안에서만 수집 — 같은 텍스트의 다른 버튼을 잡지 않기 위해(D-005)
        var texts = await page.EvaluateAsync<string[]>(@"() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const cancel = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(b => (b.innerText || '').trim() === '취소' && vis(b)).pop();
          if (!cancel) return [];
          let host = cancel.parentElement;
          while (host && host.querySelectorAll('[role=button], button, .cl-button').length < 5)
            host = host.parentElement;
          if (!host) return [];
          return [...host.querySelectorAll('[role=button], button, .cl-button')]
                 .map(b => (b.innerText || '').trim()).filter(t => t.length > 0);
        }");

        if (texts.Length == 0) return null;   // 메뉴가 안 열림 = CellUnavailable

        await CloseMenuAsync();
        return new TimetableCatalog(TimetableMenuParser.ParseAll(texts));
    }

    /// <summary>메뉴를 [취소]로 닫는다. 취소가 없으면 Escape — 어떤 경우에도 다른 항목을 누르지 않는다.</summary>
    public async Task CloseMenuAsync()
    {
        var closed = await page.EvaluateAsync<bool>(@"() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const c = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(b => (b.innerText || '').trim() === '취소' && vis(b)).pop();
          if (!c) return false;
          c.click(); return true;
        }");
        if (!closed) await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync((float)Timings.AfterOptionClick.TotalMilliseconds);
    }

    /// <summary>CLX 그리드의 데이터 행을 필드명→값 사전으로 읽는다.</summary>
    private async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> ReadClxRowsAsync(string gridId)
    {
        var json = await page.EvaluateAsync<JsonElement?>(ReadRowsJs, gridId);
        if (json is null || json.Value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"CLX 그리드 '{gridId}' 를 읽지 못했습니다.");

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var row in json.Value.EnumerateArray())
        {
            var dict = new Dictionary<string, string>();
            foreach (var p in row.EnumerateObject())
                dict[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            rows.Add(dict);
        }
        return rows;
    }

    private static bool TryYmd(IReadOnlyDictionary<string, string> row, string key, out DateOnly date)
    {
        date = default;
        return row.TryGetValue(key, out var v) && DateOnly.TryParseExact(v.Trim(), "yyyyMMdd", out date);
    }
}
