using Microsoft.Playwright;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;

namespace NeisAutoFill.Automation;

/// <summary>
/// 학기말 종합의견 화면 셀렉터 — 2026-07-07 실기기 [화면 진단]으로 확정.
/// 실측 사실: 그리드 colcount=5 / 행 = 학생당 1행 / cell1 "N행 번호 …" / cell2 "N행 성명 …" /
/// cell4 "N행 마지막 열 학기말 종합의견 편집창" 안에 CLX 커스텀 컨트롤
/// div.cl-control.cl-textarea (네이티브 textarea 아님 — 클릭하면 편집 모드로 전환).
/// </summary>
public static partial class NarrativeSelectors
{
    /// <summary>서술문 대상 그리드 후보 (스캔 스크립트가 편집 셀 보유 여부로 판별).</summary>
    public const string GridWithTextarea = "div.cl-grid[role='grid']";

    /// <summary>행 내 서술문 편집 컨트롤 — CLX 커스텀(div.cl-textarea) 우선, 네이티브 textarea 허용.</summary>
    public const string EditorInRow = "div[role='gridcell'] div.cl-textarea, div[role='gridcell'] textarea";

    /// <summary>편집 모드 진입 후 나타나는 실제 입력 요소 (콤보 팝업(§3.5)과 같은 동적 생성 패턴).</summary>
    public const string ActiveEditor = "textarea";

    public static string Row(int idx) => $"div.cl-grid-row[data-rowindex='{idx}']";

    /// <summary>번호 라벨 — "반/번호"/"번호" 둘 다, 마지막 행의 '마지막 행' 토큰 허용.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+행 (?:마지막 행 )?(?:반/)?번호 (.+?)(?:\s|$)")]
    public static partial System.Text.RegularExpressions.Regex NoFlexRegex();
}

/// <summary>
/// AI 생성 서술문을 나이스 화면 textarea 에 입력한다. 안전정책은 등급 입력과 동일:
/// dry-run, 매칭 검증, 입력 후 재검증+1회 재시도, 저장 미자동화.
/// </summary>
public sealed class NarrativeWriter(IPage page, GridScroller scroller)
{
    // 보이는 그리드 중 "textarea 셀 + 성명 aria-label 행"을 가진 그리드를 찾아
    // 그 그리드의 인덱스와 렌더된 행들의 (rowindex, 번호 aria, 성명 aria)를 반환
    private const string ScanScript = @"
() => {
  const vis = el => el.offsetParent !== null;
  const grids = [...document.querySelectorAll('div.cl-grid[role=""grid""]')].filter(vis);
  for (let gi = 0; gi < grids.length; gi++) {
    const rows = grids[gi].querySelectorAll('div.cl-grid-row[data-rowindex]');
    const out = [];
    let hasTextarea = false;
    rows.forEach(r => {
      if (r.querySelector('div[role=""gridcell""] div.cl-textarea, div[role=""gridcell""] textarea')) hasTextarea = true;
      const idx = r.getAttribute('data-rowindex');
      const cell = ci => {
        const c = r.querySelector('div[role=""gridcell""][data-cellindex=""' + ci + '""]');
        return c ? (c.getAttribute('aria-label') || '') : '';
      };
      // 성명 라벨은 셀 위치가 화면마다 다를 수 있어 행 전체에서 탐색
      let nameLabel = '', noLabel = '';
      r.querySelectorAll('div[role=""gridcell""][aria-label]').forEach(c => {
        const a = c.getAttribute('aria-label');
        if (/^\d+행 (마지막 행 )?성명 /.test(a)) nameLabel = a;
        if (/^\d+행 (마지막 행 )?(반\/)?번호 /.test(a)) noLabel = a;
      });
      out.push([idx, noLabel, nameLabel]);
    });
    if (hasTextarea && out.some(r => r[2] !== '')) {
      return { gridIndex: gi, rowcount: grids[gi].getAttribute('aria-rowcount'), rows: out };
    }
  }
  return null;
}";

    public async Task<NarrativeReport> RunAsync(
        IReadOnlyList<NarrativeEntry> entries,
        bool dryRun,
        int maxBytes,
        Action<string> log,
        Action<int, int> progressCount,
        CancellationToken ct,
        Func<IReadOnlyDictionary<int, (string? No, string? Name)>,
             Task<IReadOnlyDictionary<string, string>?>>? resolveNameMap = null)
    {
        // 1) 대상 그리드·행 지도 (스크롤 훑기로 전 행 수집)
        var (gridIndex, rowMap) = await BuildRowMapAsync(log, ct)
            ?? throw new InvalidOperationException(
                "서술문 입력 가능한 그리드(성명+textarea)를 찾지 못했습니다. " +
                "[화면 진단]을 실행해 구조를 확인해 주세요.");

        log($"서술문 대상 행 파악: {rowMap.Count}건");
        var grid = VisibleGrids().Nth(gridIndex);

        // 2) 매칭 — 이름이 달라 자동 매칭 안 되는 학생은 사용자 매핑(확인 창)으로 연결 (등급과 동일)
        IReadOnlyDictionary<string, string>? nameMap = null;
        if (resolveNameMap is not null)
        {
            nameMap = await resolveNameMap(rowMap);
            if (nameMap is null)   // 사용자 취소
            {
                log("사용자가 입력을 취소했습니다.");
                return new NarrativeReport(
                    new List<NarrativeEntry>(),
                    new List<SkipItem> { new("", "", "", "사용자 취소") },
                    new List<SkipItem>());
            }
        }

        var (todo, skipped) = NarrativeMatcher.Build(rowMap, entries, nameMap);
        var skippedList = skipped.ToList();

        var done = new List<NarrativeEntry>();
        var failed = new List<SkipItem>();
        int total = todo.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = todo[i];
            var e = item.Entry;

            // 3) 바이트 제한 사전 검사
            if (maxBytes > 0 && TextMetrics.Utf8Bytes(e.Text) > maxBytes)
            {
                skippedList.Add(new SkipItem(e.No, e.Name, "",
                    $"제한 초과 ({TextMetrics.Summary(e.Text)} > {maxBytes}바이트)"));
                log($"[{i + 1}/{total}] ⚠ {e.No}번 {e.Name}: 제한 초과로 건너뜀 ({TextMetrics.Summary(e.Text)})");
                progressCount(i + 1, total);
                continue;
            }

            if (dryRun)
            {
                done.Add(e);
                log($"[계획 {i + 1}/{total}] {e.No}번 {e.Name} ← 서술문 {TextMetrics.Summary(e.Text)}");
            }
            else
            {
                var (ok, why) = await WriteOneAsync(grid, item.RowIndex, e.Text, ct);
                if (ok)
                {
                    done.Add(e);
                    log($"[{i + 1}/{total}] ✓ {e.No}번 {e.Name} 서술문 입력 ({TextMetrics.Summary(e.Text)})");
                }
                else
                {
                    failed.Add(new SkipItem(e.No, e.Name, "", why));
                    log($"[{i + 1}/{total}] ✗ {e.No}번 {e.Name}: {why}");
                }
            }
            progressCount(i + 1, total);
        }

        return new NarrativeReport(done, skippedList, failed);
    }

    /// <summary>
    /// 한 행의 종합의견 입력 + 재검증. §4.3 과 같은 신뢰성 루프 (최대 2회).
    /// CLX 편집 패턴: 표시용 div.cl-textarea 를 네이티브 클릭 → 실제 textarea 가
    /// 동적 생성됨(콤보 팝업 §3.5 와 동일 방식) → 채우고 포커스 이탈로 확정.
    /// </summary>
    private async Task<(bool ok, string why)> WriteOneAsync(
        ILocator grid, int rowIndex, string text, CancellationToken ct)
    {
        string why = "";
        var target = NormalizeWs(text);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var editor = await EnsureEditorVisibleAsync(grid, rowIndex, ct);
            if (editor is null) return (false, "행을 화면에 못 띄움");

            try
            {
                var current = NormalizeWs(await ReadEditorTextAsync(editor));
                if (current == target) return (true, "이미 입력됨");   // 멱등성

                await editor.ClickAsync();                 // 네이티브 클릭으로 편집 모드 진입 (§3.7 원칙)

                // 편집용 실제 textarea 가 나타날 때까지 폴링 (§4.3 팝업 폴링과 동일)
                var input = await WaitForActiveEditorAsync(ct);
                if (input is null) { why = "편집기 안 열림"; continue; }

                await input.FillAsync(text);
                await page.Keyboard.PressAsync("Tab");     // 포커스 이탈로 값 확정
                await Task.Delay(Timings.AfterOptionClick, ct);

                // ★ 재검증: 표시 컨트롤을 fresh 로 다시 읽어 확인
                var verify = await GetFreshEditorAsync(grid, rowIndex);
                var val = verify is null ? "" : NormalizeWs(await ReadEditorTextAsync(verify));
                if (val == target) return (true, "");
                why = $"검증 불일치 (입력 후 {val.Length}자)";
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name;
            }
            if (attempt == 1) await Task.Delay(Timings.RetryDelay, ct);
        }
        return (false, why);
    }

    /// <summary>표시 컨트롤의 현재 텍스트 (div → innerText / 네이티브 textarea → value).</summary>
    private static async Task<string> ReadEditorTextAsync(ILocator editor) =>
        await editor.EvaluateAsync<string>(
            "el => el.tagName === 'TEXTAREA' ? el.value : (el.innerText || '')");

    /// <summary>편집 모드 진입 후 생성되는 실제 textarea 를 폴링으로 획득.</summary>
    private async Task<ILocator?> WaitForActiveEditorAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Timings.PopupPollTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var candidates = page.Locator(NarrativeSelectors.ActiveEditor);
            int n = await candidates.CountAsync();
            for (int i = 0; i < n; i++)
            {
                var c = candidates.Nth(i);
                if (await c.IsVisibleAsync()) return c;
            }
            await Task.Delay(Timings.PopupPollStep, ct);
        }
        return null;
    }

    private async Task<(int gridIndex, Dictionary<int, (string?, string?)> rowMap)?>
        BuildRowMapAsync(Action<string> log, CancellationToken ct)
    {
        var map = new Dictionary<int, (string?, string?)>();
        int gridIndex = -1;
        int rowcount = 0;

        async Task<bool> ScanAsync()
        {
            var result = await page.EvaluateAsync<System.Text.Json.JsonElement?>(ScanScript);
            if (result is not { ValueKind: System.Text.Json.JsonValueKind.Object } r) return false;
            gridIndex = r.GetProperty("gridIndex").GetInt32();
            int.TryParse(r.GetProperty("rowcount").GetString(), out rowcount);
            foreach (var row in r.GetProperty("rows").EnumerateArray())
            {
                if (!int.TryParse(row[0].GetString(), out var idx) || map.ContainsKey(idx)) continue;
                var no = MatchGroup(NarrativeSelectors.NoFlexRegex(), row[1].GetString());
                var name = MatchGroup(NeisSelectors.NameRegex, row[2].GetString());
                if (name is not null) map[idx] = (no, name);
            }
            return true;
        }

        if (!await ScanAsync()) return null;

        // 첫 스캔으로 대상 그리드가 정해졌으니 스크롤은 그 그리드 스코프로
        var gridScope = VisibleGrids().Nth(gridIndex);

        // 가상 스크롤 훑기 (§4.2와 동일 패턴)
        var vscroll = await scroller.GetVScrollAsync(gridScope);
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
            await scroller.ScrollProxyAsync(v.bar, 0);
            await Task.Delay(Timings.AfterScroll, ct);
        }

        // §8 마지막 행: 누락 인덱스를 CLX 공식 reveal API 로 복구
        int expected = Math.Max(rowcount - 1, 0);
        if (expected > 0)
        {
            var missing = Enumerable.Range(0, expected).Where(i => !map.ContainsKey(i)).ToList();
            foreach (var idx in missing.Take(10))
            {
                ct.ThrowIfCancellationRequested();
                var res = await ClxGridApi.RevealAsync(page, expected, idx);
                log($"  누락 행 {idx} → CLX reveal ({res})");
                await Task.Delay(Timings.AfterScroll, ct);
                await ScanAsync();
            }
            missing = Enumerable.Range(0, expected).Where(i => !map.ContainsKey(i)).ToList();
            if (missing.Count > 0)
                log($"  진단(서술문 행지도): 파악 {map.Count}/{expected}, 누락 [{string.Join(",", missing)}]");
        }

        return map.Count > 0 ? (gridIndex, map) : null;
    }

    private ILocator VisibleGrids() =>
        page.Locator(NarrativeSelectors.GridWithTextarea).Locator("visible=true");

    private async Task<ILocator?> GetFreshEditorAsync(ILocator grid, int rowIndex)
    {
        var ed = grid.Locator(NarrativeSelectors.Row(rowIndex))
                     .Locator(NarrativeSelectors.EditorInRow);
        if (await ed.CountAsync() == 0) return null;
        var first = ed.First;
        return await first.IsVisibleAsync() ? first : null;
    }

    private async Task<ILocator?> EnsureEditorVisibleAsync(ILocator grid, int idx, CancellationToken ct)
    {
        var ed = await GetFreshEditorAsync(grid, idx);
        if (ed is null)
        {
            var vs = await scroller.GetVScrollAsync(grid);
            if (vs is { } v)
            {
                var rowcountAttr = await grid.GetAttributeAsync("aria-rowcount") ?? "1";
                int expected = Math.Max(int.Parse(rowcountAttr) - 1, 1);
                double approx = v.scrollHeight * idx / expected - v.clientHeight / 2;
                await scroller.ScrollProxyAsync(v.bar, Math.Max(approx, 0));
                await Task.Delay(Timings.AfterScroll, ct);
                ed = await GetFreshEditorAsync(grid, idx);
            }
        }
        if (ed is null)   // 끝행 대비 (§8): CLX 공식 reveal API 로 행 직접 가시화
        {
            var rowcountAttr2 = await grid.GetAttributeAsync("aria-rowcount") ?? "1";
            int expected2 = Math.Max(int.Parse(rowcountAttr2) - 1, 1);
            await ClxGridApi.RevealAsync(page, expected2, idx);
            await Task.Delay(Timings.AfterScroll, ct);
            ed = await GetFreshEditorAsync(grid, idx);
        }
        if (ed is not null)
        {
            await ed.ScrollIntoViewIfNeededAsync();
            await Task.Delay(Timings.AfterScrollIntoView, ct);
            ed = await GetFreshEditorAsync(grid, idx);
        }
        return ed;
    }

    private static string? MatchGroup(System.Text.RegularExpressions.Regex re, string? label)
    {
        var m = re.Match((label ?? "").Trim());
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string NormalizeWs(string s) =>
        string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd())).Trim();
}
