using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>
/// 나이스 화면 구조 진단 도구 (diag_capture.py 상당의 C# 판).
/// 교과학습발달상황 등 미실측 화면에서 실행해 그리드/셀/텍스트영역 구조를 덤프한다.
/// 결과로 NarrativeSelectors 의 잠정 셀렉터를 확정하는 것이 목적. 읽기 전용 — 아무것도 조작하지 않음.
/// </summary>
public sealed class DomInspector(IPage page)
{
    private const string InspectScript = @"
() => {
  const vis = el => el.offsetParent !== null;
  const report = { url: location.href, title: document.title, grids: [], combos: [], editors: [] };

  // 1) 보이는 CLX 그리드 전부: 열/행 수 + 첫 데이터행의 셀 구성
  document.querySelectorAll('div.cl-grid[role=""grid""]').forEach(g => {
    if (!vis(g)) return;
    const grid = {
      colcount: g.getAttribute('aria-colcount'),
      rowcount: g.getAttribute('aria-rowcount'),
      rows_rendered: 0,
      sample_cells: []
    };
    const rows = g.querySelectorAll('div.cl-grid-row[data-rowindex]');
    grid.rows_rendered = rows.length;
    if (rows.length > 0) {
      rows[0].querySelectorAll('div[role=""gridcell""]').forEach(c => {
        grid.sample_cells.push({
          cellindex: c.getAttribute('data-cellindex'),
          aria: (c.getAttribute('aria-label') || '').substring(0, 60),
          has_textarea: !!c.querySelector('textarea'),
          has_input: !!c.querySelector('input'),
          has_combobox: !!c.querySelector('[role=""combobox""]'),
          text: (c.textContent || '').trim().substring(0, 40)
        });
      });
    }
    report.grids.push(grid);
  });

  // 2) 보이는 콤보박스 aria-label (조회조건 파악용)
  document.querySelectorAll('div[role=""combobox""][aria-label]').forEach(c => {
    if (vis(c)) report.combos.push(c.getAttribute('aria-label').substring(0, 50));
  });

  // 2.5) 보이는 버튼 전부 (전과목 자동화의 [조회]/[저장] 셀렉터 확정용)
  report.buttons = [];
  document.querySelectorAll('[role=""button""], button').forEach(b => {
    if (!vis(b)) return;
    const aria = (b.getAttribute('aria-label') || '').trim();
    const text = (b.textContent || '').trim();
    const name = aria || text;
    if (!name) return;
    report.buttons.push({
      name: name.substring(0, 40),
      tag: b.tagName.toLowerCase(),
      cls: (b.className || '').toString().substring(0, 50),
      inDialog: !!b.closest('.cl-dialog, [role=""dialog""], .cl-alert, .cl-messagebox'),
    });
  });

  // 3) 그리드 밖 텍스트 편집기 후보 (단일 상세 편집 폼 화면 대비)
  document.querySelectorAll('textarea, [contenteditable=""true""], div.cl-inputbox, div.cl-textarea').forEach(t => {
    if (!vis(t)) return;
    report.editors.push({
      tag: t.tagName.toLowerCase(),
      cls: (t.className || '').toString().substring(0, 60),
      aria: (t.getAttribute('aria-label') || '').substring(0, 60),
      readonly: t.readOnly === true || t.getAttribute('aria-readonly') === 'true',
      in_grid: !!t.closest('div.cl-grid')
    });
  });

  return report;
}";

    // §8 마지막 행 원인 분석: 바닥에서 렌더된 각 행의 셀 aria-label 을 전부 덤프하고,
    // CLX reveal(마지막행) 호출 전후의 viewing 인덱스·행 상태를 계측 (끝나면 원위치 복귀)
    private const string ScrollProbeScript = @"
async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const vis = el => el.offsetParent !== null;
  const grid = [...document.querySelectorAll('div.cl-grid[role=""grid""]')].find(vis);
  if (!grid) return { err: '보이는 그리드 없음' };

  const bar = [...grid.querySelectorAll('div.cl-grid-detail-band .cl-blank .cl-scrollbar')]
    .find(b => b.scrollHeight > b.clientHeight);
  if (!bar) return { err: '프록시 스크롤바 없음' };

  // 행별 상세 덤프: rowindex + 클래스 + 모든 셀 aria-label
  const dumpRows = () => [...grid.querySelectorAll('div.cl-grid-row[data-rowindex]')]
    .filter(vis)
    .sort((a,b) => parseInt(a.getAttribute('data-rowindex')) - parseInt(b.getAttribute('data-rowindex')))
    .map(r => ({
      idx: r.getAttribute('data-rowindex'),
      cls: (r.className || '').toString().substring(0, 60),
      labels: [...r.querySelectorAll('div[role=""gridcell""][aria-label]')]
        .map(c => (c.getAttribute('aria-label') || '').substring(0, 45)),
    }));

  const expected = parseInt(grid.getAttribute('aria-rowcount')) - 1;
  const out = {
    rowcount: grid.getAttribute('aria-rowcount'),
    scrollHeight: bar.scrollHeight, clientHeight: bar.clientHeight,
  };
  const origin = bar.scrollTop;

  // 1) 프록시 바닥 → 렌더 행 상세
  bar.scrollTop = bar.scrollHeight;
  bar.dispatchEvent(new Event('scroll', {bubbles:true}));
  await sleep(500);
  out.bottomRows = dumpRows();

  // 2) CLX reveal(마지막 행) 전후 비교
  try {
    if (typeof cpr !== 'undefined') {
      const apps = cpr.core.Platform.INSTANCE.getAllRunningAppInstances();
      const seen = new Set(); const grids = [];
      const visit = c => {
        if (!c || seen.has(c)) return; seen.add(c);
        if (typeof c.type === 'string' && c.type === 'grid') grids.push(c);
        try { if (c.getChildren) c.getChildren().forEach(visit); } catch (e) {}
        try { if (c.content && c.content.getChildren) c.content.getChildren().forEach(visit); } catch (e) {}
        try { if (c.getEmbeddedAppInstance) { const ea = c.getEmbeddedAppInstance(); if (ea && ea.getContainer) visit(ea.getContainer()); } } catch (e) {}
      };
      apps.forEach(a => { try { visit(a.getContainer()); } catch (e) {} });
      const targets = grids.filter(g => { try { return g.getDataRowCount && g.getDataRowCount() === expected; } catch (e) { return false; } });
      out.clx = targets.map(g => {
        const info = { id: g.id || '' };
        try { info.dataRows = g.getDataRowCount(); } catch (e) {}
        try { info.rowCount = g.getRowCount(); } catch (e) {}
        try { info.viewingBefore = [g.getViewingStartRowIndex(), g.getViewingEndRowIndex()]; } catch (e) {}
        try { info.rowStateLast = String(g.getRowState ? g.getRowState(expected - 1) : ''); } catch (e) { info.rowStateLast = 'err'; }
        try { g.reveal(expected - 1); info.revealed = true; } catch (e) { info.revealed = 'err:' + e; }
        return info;
      });
      await sleep(500);
      out.clx.forEach((info, i) => {
        try { info.viewingAfter = [targets[i].getViewingStartRowIndex(), targets[i].getViewingEndRowIndex()]; } catch (e) {}
      });
      out.afterReveal = dumpRows();
    }
  } catch (e) { out.clxErr = String(e); }

  // 원위치 복귀
  bar.scrollTop = origin;
  bar.dispatchEvent(new Event('scroll', {bubbles:true}));
  return out;
}";

    // §8.4 ③ CLX(eXBuilder6) 내부 API 탐사: 그리드 컨트롤을 찾아 스크롤 관련 메서드 목록 확보
    private const string ClxProbeScript = @"
() => {
  const out = { hasCpr: typeof window.cpr !== 'undefined' };
  if (!out.hasCpr) return out;
  try {
    const platform = cpr.core.Platform.INSTANCE;
    out.platformKeys = Object.getOwnPropertyNames(Object.getPrototypeOf(platform))
      .filter(n => /app|instance|lookup/i.test(n)).slice(0, 20);

    const apps = platform.getAllRunningAppInstances ? platform.getAllRunningAppInstances() : [];
    out.appCount = apps.length;

    const grids = [];
    const typeCounts = {};
    const seen = new Set();
    const visit = c => {
      if (!c || seen.has(c)) return;
      seen.add(c);
      const tn = (c.constructor && c.constructor.name) || '';
      const ty = (typeof c.type === 'string') ? c.type : '';
      const key = ty || tn || '?';
      typeCounts[key] = (typeCounts[key] || 0) + 1;
      if (/grid/i.test(tn) || /grid/i.test(ty)) grids.push(c);
      try { if (c.getChildren) c.getChildren().forEach(visit); } catch (e) {}
      try { if (c.content && c.content.getChildren) c.content.getChildren().forEach(visit); } catch (e) {}
      try { if (c.getEmbeddedAppInstance) { const ea = c.getEmbeddedAppInstance(); if (ea && ea.getContainer) visit(ea.getContainer()); } } catch (e) {}
    };
    apps.forEach(a => {
      try { visit(a.getContainer ? a.getContainer() : null); } catch (e) {}
    });
    out.controlTypes = typeCounts;
    out.gridControls = grids.length;
    if (grids.length > 0) {
      const g = grids[0];
      out.gridCtor = (g.constructor && g.constructor.name) || '';
      out.gridId = g.id || '';
      const names = new Set();
      let p = Object.getPrototypeOf(g);
      while (p && names.size < 200) {
        Object.getOwnPropertyNames(p).forEach(n => names.add(n));
        p = Object.getPrototypeOf(p);
      }
      out.scrollMethods = [...names]
        .filter(n => /scroll|visible|topindex|reveal|show.*row|row.*index/i.test(n)).slice(0, 40);
      out.rowMethods = [...names].filter(n => /^get.*row|^set.*row/i.test(n)).slice(0, 40);
    }
  } catch (e) { out.err = String(e); }
  return out;
}";

    /// <summary>현재 화면 구조를 사람이 읽을 수 있는 텍스트 리포트로 반환.</summary>
    public async Task<string> InspectAsync()
    {
        var json = await page.EvaluateAsync<JsonElement>(InspectScript);

        var sb = new StringBuilder();
        sb.AppendLine("═══ NEIS 화면 구조 진단 ═══");
        sb.AppendLine($"URL: {json.GetProperty("url").GetString()}");
        sb.AppendLine($"제목: {json.GetProperty("title").GetString()}");

        sb.AppendLine($"\n── 보이는 그리드 {json.GetProperty("grids").GetArrayLength()}개 ──");
        foreach (var g in json.GetProperty("grids").EnumerateArray())
        {
            sb.AppendLine($"grid colcount={g.GetProperty("colcount")} rowcount={g.GetProperty("rowcount")} " +
                          $"렌더행={g.GetProperty("rows_rendered")}");
            foreach (var c in g.GetProperty("sample_cells").EnumerateArray())
            {
                var flags = new List<string>();
                if (c.GetProperty("has_textarea").GetBoolean()) flags.Add("TEXTAREA");
                if (c.GetProperty("has_input").GetBoolean()) flags.Add("INPUT");
                if (c.GetProperty("has_combobox").GetBoolean()) flags.Add("COMBO");
                sb.AppendLine($"  cell[{c.GetProperty("cellindex")}] {string.Join("+", flags),-16} " +
                              $"aria='{c.GetProperty("aria")}' text='{c.GetProperty("text")}'");
            }
        }

        sb.AppendLine($"\n── 보이는 콤보박스 ──");
        foreach (var c in json.GetProperty("combos").EnumerateArray())
            sb.AppendLine($"  {c.GetString()}");

        sb.AppendLine($"\n── 보이는 버튼 ──");
        foreach (var b in json.GetProperty("buttons").EnumerateArray())
            sb.AppendLine($"  [{b.GetProperty("name").GetString()}] <{b.GetProperty("tag").GetString()}> " +
                          $"cls='{b.GetProperty("cls").GetString()}' 대화상자내부={b.GetProperty("inDialog")}");

        sb.AppendLine($"\n── 텍스트 편집기 후보 ──");
        foreach (var e in json.GetProperty("editors").EnumerateArray())
            sb.AppendLine($"  <{e.GetProperty("tag")}> cls='{e.GetProperty("cls")}' " +
                          $"aria='{e.GetProperty("aria")}' readonly={e.GetProperty("readonly")} " +
                          $"그리드내부={e.GetProperty("in_grid")}");

        // §8 스크롤 경계 분석 (프록시 바닥 이동 1회 후 원위치 복귀)
        sb.AppendLine("\n── 스크롤 경계 분석 (§8 마지막 행) ──");
        try
        {
            var probe = await page.EvaluateAsync<JsonElement>(ScrollProbeScript);
            sb.AppendLine(probe.ToString());
        }
        catch (Exception ex) { sb.AppendLine($"  분석 실패: {ex.Message}"); }

        // §8.4 ③ CLX 내부 API 탐사
        sb.AppendLine("\n── CLX 내부 API 탐사 ──");
        try
        {
            var clx = await page.EvaluateAsync<JsonElement>(ClxProbeScript);
            sb.AppendLine(clx.ToString());
        }
        catch (Exception ex) { sb.AppendLine($"  탐사 실패: {ex.Message}"); }

        return sb.ToString();
    }
}
