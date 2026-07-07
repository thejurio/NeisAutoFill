using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>
/// CLX(eXBuilder6) 그리드 컨트롤의 공식 API 호출 (§8.4 ③ — 2026-07-07 실기기 탐사로 확정).
/// 프록시 스크롤바 흉내는 마지막 행 경계에서 불안정하므로(§8), 프레임워크의
/// reveal(rowIndex) 로 행을 직접 가시화한다. 대상 그리드는 데이터 행수 일치로 특정.
/// </summary>
public static class ClxGridApi
{
    private const string RevealScript = @"
(args) => {
  const expected = args[0], idx = args[1];
  try {
    if (typeof cpr === 'undefined') return 'no-cpr';
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

    let hit = 0;
    grids.forEach(g => {
      try {
        if (g.getDataRowCount && g.getDataRowCount() === expected && typeof g.reveal === 'function') {
          g.reveal(idx);
          hit++;
        }
      } catch (e) {}
    });
    return 'revealed:' + hit + '/' + grids.length;
  } catch (e) { return 'err:' + String(e); }
}";

    /// <summary>
    /// 데이터 행수가 expected 인 CLX 그리드에 reveal(rowIndex) 호출.
    /// 반환 예: "revealed:1/9" (성공한 그리드 수 / 전체 그리드 수).
    /// </summary>
    public static Task<string> RevealAsync(IPage page, int expectedRows, int rowIndex) =>
        page.EvaluateAsync<string>(RevealScript, new object[] { expectedRows, rowIndex });
}
