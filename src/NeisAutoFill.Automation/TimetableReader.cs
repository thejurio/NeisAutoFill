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
/// <item>DOM 에 grdWeekByClsByTi id 는 없다 → <b>구조</b>로 찾는다(첫 셀이 "N교시")</item>
/// <item>aria-colcount/rowcount 는 논리 수(8×8)와 다르다 → 세는 데 쓰지 않는다</item>
/// <item>메뉴 항목 id(uuid-*)는 매번 바뀐다 → 텍스트 구조와 안정 키만 쓴다(D-004)</item>
/// <item>메뉴는 반드시 [취소]로 닫는다 — 다른 항목을 누르면 데이터가 바뀐다</item>
/// </list>
/// </summary>
public sealed class TimetableReader(IPage page)
{
    /// <summary>
    /// 주별 학급시간표 그리드를 구조로 찾는다. 못 찾으면 -1.
    ///
    /// <b>요일 칸 수를 못 박지 않는다</b> — 학교·설정에 따라 월~금(6칸)일 수도 월~일(8칸)일 수도 있다.
    /// 8칸으로 고정했다가 월~금만 나오는 학교에서 "그리드를 찾지 못했습니다"로 막혔다(실측 2026-08-20).
    /// 첫 칸이 "N교시"인 것이 이 표를 알아보는 진짜 표식이다.
    /// </summary>
    private const string FindGridJs = @"() => {
  const grids = [...document.querySelectorAll('div.cl-grid[role=grid]')];
  for (let i = 0; i < grids.length; i++) {
    const row = grids[i].querySelector('div.cl-grid-row[data-rowindex]');
    if (!row) continue;
    const cells = row.querySelectorAll('div[role=gridcell]');
    if (cells.length < 6 || cells.length > 9) continue;                 // 교시 + 월~금(5) ~ 월~일(7)
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

    /// <summary>
    /// 현재 화면이 학급시간표관리인지. 제목으로 판정한다 —
    /// 실측: 메뉴 라벨은 "학급시간표관리"(붙임)인데 화면 제목은 <b>"학급시간표 관리"(띄어쓰기)</b> 라
    /// 두 표기에 모두 걸리는 "학급시간표" 로 비교한다.
    /// 이동 직후에는 그리드가 비어 있으므로 <b>그리드 유무로 판정하면 안 된다</b>.
    /// </summary>
    public async Task<bool> IsTimetableScreenAsync() =>
        await page.EvaluateAsync<bool>(@"() => [...document.querySelectorAll('div.app-tit')]
            .some(t => { const r = t.getBoundingClientRect();
                         return r.width > 0 && r.height > 0 && (t.innerText || '').includes('학급시간표'); })");

    /// <summary>
    /// 셀을 우클릭할 때 기다릴 시간.
    /// 없는 셀을 30초씩 기다리면 요일마다 멈춰 프로그램이 멎은 것처럼 보인다.
    /// </summary>
    private static readonly TimeSpan CellClickTimeout = TimeSpan.FromSeconds(4);

    /// <summary>시간표 그리드가 실제로 그려져 있는지 (조회·주차 선택이 끝났는지).</summary>
    public async Task<bool> HasGridAsync() => await page.EvaluateAsync<int>(FindGridJs) >= 0;

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

        if (!await ScrollWeekRowIntoViewAsync(weekGrid, weekRowIndex))
            throw new InvalidOperationException(
                $"주차 목록에서 {weekRowIndex + 1}번째 주를 화면에 띄우지 못했습니다.");

        await page.Locator($"div.cl-grid[role=grid] >> nth={weekGrid} >> " +
                           $"div.cl-grid-row[data-rowindex='{weekRowIndex}'] div[role=gridcell] >> nth=0")
                  .ClickAsync(new LocatorClickOptions { Timeout = 5000 });
        await page.WaitForTimeoutAsync((float)Timings.TimetableWeekChange.TotalMilliseconds);

        // 미저장 변경이 있으면 여기서 저장 확인 대화상자가 뜬다 — 누르지 않고 알린다
        if (await HasSaveDialogAsync())
            throw new InvalidOperationException(
                "저장하지 않은 변경이 있어 주차를 옮길 수 없습니다. " +
                "나이스에서 저장하거나 변경을 버린 뒤 다시 시도하세요.");
    }

    /// <summary>
    /// 주차 목록에서 그 행이 실제로 그려지도록 스크롤한다.
    ///
    /// <b>주차 목록은 가상 스크롤이다</b> — 28주짜리 학기여도 DOM 에는 18행 정도만 있고,
    /// 나머지는 스크롤해야 생긴다. 이걸 모르면 12월 이후 주차에서 클릭이 통째로 실패한다(실측 2026-08-19).
    /// </summary>
    private async Task<bool> ScrollWeekRowIntoViewAsync(int gridIndex, int rowIndex)
    {
        const string ScrollJs = @"(a) => {
          const grid = [...document.querySelectorAll('div.cl-grid[role=grid]')][a.gridIndex];
          if (!grid) return 'no-grid';
          if (grid.querySelector(`div.cl-grid-row[data-rowindex='${a.rowIndex}']`)) return 'present';

          const rows = [...grid.querySelectorAll('div.cl-grid-row[data-rowindex]')];
          if (rows.length === 0) return 'no-rows';

          const shown = rows.map(r => +r.getAttribute('data-rowindex'));
          const min = Math.min(...shown), max = Math.max(...shown);
          const height = rows[0].getBoundingClientRect().height || 24;

          // 실제로 스크롤되는 안쪽 요소를 찾는다 (그리드 자신일 수도 있다)
          const scroller = [grid, ...grid.querySelectorAll('div')]
            .filter(e => e.scrollHeight > e.clientHeight + 4)
            .sort((x, y) => y.scrollHeight - x.scrollHeight)[0];
          if (!scroller) return 'no-scroller';

          // 한 화면씩 넘어가지 말고 목표 근처로 바로 보낸다 (여유 한 줄)
          scroller.scrollTop += a.rowIndex < min
            ? (a.rowIndex - min - 1) * height
            : (a.rowIndex - max + 1) * height;

          return 'scrolled';
        }";

        for (var attempt = 0; attempt < 12; attempt++)
        {
            var state = await page.EvaluateAsync<string>(ScrollJs, new { gridIndex, rowIndex });

            if (state == "present") return true;
            if (state is "no-grid" or "no-rows" or "no-scroller") return false;

            await page.WaitForTimeoutAsync(200);   // 가상 스크롤이 행을 그릴 시간
        }

        return false;
    }

    /// <summary>
    /// 해당 날짜가 들어 있는 주차를 골라 시간표를 띄운다.
    /// 조회 직후에는 주차 목록만 있고 그리드가 비어 있으므로, 읽기 전에 반드시 한 번 호출해야 한다.
    /// 주차 목록의 범위는 <b>수업일</b> 기준이라 월~일 전체와 다르다 — 주 시작일을 월요일로 맞춰 비교한다.
    /// </summary>
    /// <returns>고른 주차. 해당 날짜의 주차를 찾지 못하면 null</returns>
    public async Task<TimetableWeek?> SelectWeekForDateAsync(DateOnly date)
    {
        var weeks = await ReadWeeksAsync();
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

        var week = weeks.FirstOrDefault(w =>
            (date >= w.Start && date <= w.End) ||                                   // 수업일 범위 안
            w.Start.AddDays(-(((int)w.Start.DayOfWeek + 6) % 7)) == monday);        // 같은 주(월요일 기준)

        if (week is null) return null;

        // 이미 그 주가 떠 있으면 클릭하지 않는다.
        // 미저장 변경이 있는 채로 주차를 누르면 "저장하시겠습니까?" 대화상자가 떠 멈춘다(실측).
        var current = await ReadCurrentWeekAsync();
        if (current.Dates.Contains(date)) return week;

        await SelectWeekAsync(week.Index);
        return week;
    }

    /// <summary>
    /// 셀 하나를 우클릭해 할당 가능한 과목·교사 목록(카탈로그)을 읽는다.
    /// <b>반드시 [취소]로 닫는다.</b> 메뉴가 열리지 않으면 null — 학기 시작 전·휴업일의 정상 동작이다.
    /// </summary>
    /// <param name="closeAfter">읽은 뒤 메뉴를 닫을지. 입력을 이어서 할 때는 false 로 두고 호출부가 책임진다.</param>
    public async Task<TimetableCatalog?> ReadCatalogAsync(int rowIndex, int dayColumn, bool closeAfter = true)
    {
        var gridIdx = await page.EvaluateAsync<int>(FindGridJs);
        if (gridIdx < 0) throw new InvalidOperationException("시간표 그리드를 찾지 못했습니다.");

        // 셀이 없으면 <b>빨리 포기한다</b>. 기본 타임아웃(30초)을 그대로 두면
        // 학기 시작 전 요일 하나에서 30초씩 멈췄다가 오류창이 뜬다 — 다섯 요일이면 2분 반이다(실측).
        // 못 누르는 것은 실패가 아니라 "이 날은 메뉴가 안 열린다" 이므로 null 로 돌려준다.
        try
        {
            await page.Locator($"div.cl-grid[role=grid] >> nth={gridIdx} >> " +
                               $"div.cl-grid-row[data-rowindex='{rowIndex}'] div[role=gridcell] >> nth={dayColumn}")
                      .ClickAsync(new LocatorClickOptions
                      {
                          Button = MouseButton.Right,
                          Timeout = (float)CellClickTimeout.TotalMilliseconds,
                      });
        }
        catch (TimeoutException)
        {
            return null;
        }

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

        if (closeAfter) await CloseMenuAsync();
        return new TimetableCatalog(TimetableMenuParser.ParseAll(texts));
    }

    /// <summary>
    /// 그 좌표에 지금 있는 셀의 날짜를 읽는다 (클릭 직전 교차 확인용).
    /// 화면이 다른 주로 바뀌었으면 여기서 어긋난 날짜가 나와 입력을 막을 수 있다.
    /// </summary>
    public async Task<DateOnly?> ReadCellDateAsync(int rowIndex, int dayColumn)
    {
        var rows = await ReadClxRowsAsync("grdWeekByClsByTi");
        if (rowIndex < 0 || rowIndex >= rows.Count) return null;

        // dayColumn 1=월 … 7=일
        string[] prefixes = { "mon", "tue", "wed", "thu", "fri", "sat", "sun" };
        if (dayColumn < 1 || dayColumn > prefixes.Length) return null;

        return rows[rowIndex].TryGetValue(prefixes[dayColumn - 1] + "Ymd", out var ymd)
            && DateOnly.TryParseExact(ymd.Trim(), "yyyyMMdd", out var d) ? d : null;
    }

    /// <summary>
    /// 저장 확인 대화상자가 떠 있는지 (실측 2026-08-19).
    /// 미저장 변경이 있는 채로 조회·주차 이동을 하면 나이스가 이걸 띄운다:
    /// <c>"시간표에 변경된 항목이 존재합니다. 조회전에 저장하셔야합니다. 저장하시겠습니까?"</c>
    /// <b>[확인]을 누르면 저장된다</b> — 자동으로 누르지 않는다(D-010, 실기기검증 §11).
    /// </summary>
    public async Task<bool> HasSaveDialogAsync() =>
        await page.EvaluateAsync<bool>(@"() => [...document.querySelectorAll('[role=button], button, .cl-button')]
            .some(b => {
                const r = b.getBoundingClientRect();
                if (!(r.width > 0 && r.height > 0)) return false;
                for (let e = b.parentElement; e; e = e.parentElement)
                    if ((e.innerText || '').includes('저장하시겠습니까')) return true;
                return false;
            })");

    /// <summary>
    /// 저장 확인 대화상자를 <b>[취소]</b>로 닫는다 — 저장하지 않고 변경을 버린다.
    /// [확인]은 이 클래스에서 절대 누르지 않는다. 저장은 별도 승인 경로에서만 한다.
    /// </summary>
    public async Task<bool> DismissSaveDialogAsync()
    {
        var ok = await page.EvaluateAsync<bool>(@"() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const cancel = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(vis)
            .filter(b => (b.innerText || '').trim() === '취소')
            .filter(b => { for (let e = b.parentElement; e; e = e.parentElement)
                             if ((e.innerText || '').includes('저장하시겠습니까')) return true;
                           return false; })
            .pop();
          if (!cancel) return false;
          cancel.click();
          return true;
        }");

        if (ok) await page.WaitForTimeoutAsync((float)Timings.TimetableWeekChange.TotalMilliseconds);
        return ok;
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
