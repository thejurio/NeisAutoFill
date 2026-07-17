using Microsoft.Playwright;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Automation;

/// <summary>
/// 나이스 교과별 평가 자동입력 엔진 (Playwright). 개발노트 §4 알고리즘 이식.
/// 안전정책: 과목 검증, dry-run, 척도 화이트리스트, 입력 후 재검증+재시도, 저장 미자동화, 멱등성.
/// </summary>
public sealed class NeisEngine(EngineOptions options) : INeisEngine, IAsyncDisposable
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IPage? _page;
    private GridScroller? _scroller;
    private ComboBoxDriver? _combo;

    public bool Connected => _page is not null;

    public void LaunchEdge() => new EdgeLauncher(options).Launch();

    public async Task<bool> AttachAsync(CancellationToken ct = default)
    {
        _pw ??= await Playwright.CreateAsync();
        _browser = await _pw.Chromium.ConnectOverCDPAsync($"http://{options.DebugAddress}");

        foreach (var context in _browser.Contexts)
        {
            foreach (var page in context.Pages)
            {
                if (page.Url.Contains("neis.go.kr"))
                {
                    _page = page;
                    _scroller = new GridScroller(page);
                    _combo = new ComboBoxDriver(page);
                    return true;
                }
            }
        }
        throw new InvalidOperationException("neis.go.kr 탭을 찾지 못했습니다. NEIS에 접속해 주세요.");
    }

    /// <summary>연결이 살아있는지 확인. 죽었으면 상태를 비우고 false. (자동 재연결 루프용)</summary>
    public async Task<bool> IsAliveAsync()
    {
        if (_page is null) return false;
        try
        {
            _ = await _page.TitleAsync();          // 페이지 응답 확인
            if (_page.IsClosed) throw new InvalidOperationException("page closed");
            if (!_page.Url.Contains("neis.go.kr")) return true;   // 다른 탭으로 이동해도 연결 유지
            return true;
        }
        catch
        {
            _page = null; _scroller = null; _combo = null;
            try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
            _browser = null;
            return false;
        }
    }

    public async Task<string?> GetCurrentSubjectAsync(CancellationToken ct = default)
    {
        var page = RequirePage();
        var combos = page.Locator(NeisSelectors.SubjectCombo);
        int n = await combos.CountAsync();
        for (int i = 0; i < n; i++)
        {
            var c = combos.Nth(i);
            if (!await c.IsVisibleAsync()) continue;
            var label = await c.GetAttributeAsync("aria-label") ?? "";
            var parts = label.Split(',', 2);
            if (parts.Length == 2) return parts[1].Trim();
        }
        return null;
    }

    public async Task<RunReport> RunSubjectAsync(
        SubjectSheet sheet,
        GradeScale scale,
        bool dryRun,
        IProgress<ProgressInfo> progress,
        Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null,
        CancellationToken ct = default)
    {
        var page = RequirePage();
        void Log(string m) => progress.Report(new ProgressInfo(m));

        // 화면 과목 확인 — 콜백이 없으면 예전처럼 즉시 차단, 있으면 UI 가 결정
        var subject = await GetCurrentSubjectAsync(ct);
        if (resolveMatch is null && subject != sheet.SubjectName)
            throw new InvalidOperationException(
                $"화면 교과 '{subject}' ≠ 대상 '{sheet.SubjectName}'. 나이스에서 '{sheet.SubjectName}'를 조회하세요.");

        var grid = await FindGridAsync(page)
            ?? throw new InvalidOperationException("평가 그리드를 찾지 못했습니다. [조회]를 눌렀는지 확인하세요.");
        int expected = int.Parse(await grid.GetAttributeAsync("aria-rowcount") ?? "1") - 1;

        Log($"엑셀 영역: {string.Join(", ", sheet.Areas.Select(a => $"'{a}'"))}");
        Log($"그리드 {expected}행 / 행 위치 파악 중...");
        var rowMap = await new RowMapBuilder(page, _scroller!).BuildAsync(grid, expected, Log, ct);
        var missing = Enumerable.Range(0, expected).Where(i => !rowMap.ContainsKey(i)).ToList();
        Log($"행 파악 {rowMap.Count}/{expected}" + (missing.Count > 0 ? $" (누락: {string.Join(",", missing)})" : ""));

        // 매칭 결정 — UI 콜백이 검토 (문제 없으면 창 없이 즉시, 있으면 미리보기 창)
        MatchDecision? decision;
        if (resolveMatch is not null)
        {
            decision = await resolveMatch(new MatchContext(subject, sheet.SubjectName, rowMap, missing));
            if (decision is null)
            {
                Log("사용자가 입력을 취소했습니다.");
                return new RunReport(new List<GradeTask>(),
                    new List<SkipItem> { new("", "", "", "사용자 취소") },
                    new List<SkipItem>(), missing);
            }
        }
        else
        {
            decision = new MatchDecision(StudentMatcher.MatchMode.ByName);
        }

        var excelAreas = decision.Mode == StudentMatcher.MatchMode.ByOrder
            ? decision.OrderedExcelAreas ?? sheet.Areas
            : sheet.Areas;
        var (todo, skipped, actualMode, fatal) = StudentMatcher.Build(
            rowMap, sheet.Students, scale, excelAreas, decision.Mode,
            decision.AreaMap, decision.NameMap);
        if (fatal is not null)
            throw new InvalidOperationException(fatal);
        Log($"매칭 방식: {(actualMode == StudentMatcher.MatchMode.ByOrder ? "순서 기반" : "이름 기반")} / 입력 대상 {todo.Count}건");

        var done = new List<GradeTask>();
        var failed = new List<SkipItem>();
        int total = todo.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = todo[i];
            if (dryRun)
            {
                done.Add(t);
                Log($"[계획 {i + 1}/{total}] {t.No}번 {t.Name} {t.Area} → {t.TargetGrade}");
            }
            else
            {
                var (ok, why) = await SetGradeAsync(grid, t, scale, ct);
                if (ok)
                {
                    done.Add(t);
                    Log($"[{i + 1}/{total}] ✓ {t.No}번 {t.Name} {t.Area} → {t.TargetGrade}");
                }
                else
                {
                    failed.Add(new SkipItem(t.No, t.Name, t.Area, why));
                    Log($"[{i + 1}/{total}] ✗ {t.No}번 {t.Name} {t.Area}: {why}");
                }
            }
            progress.Report(new ProgressInfo("", i + 1, total));
        }

        return new RunReport(done, skipped, failed, missing);
    }

    /// <summary>단계 설정 신뢰성 루프 (§4.3). 멱등성 + 재검증 + 1회 재시도.</summary>
    private async Task<(bool ok, string why)> SetGradeAsync(
        ILocator grid, GradeTask task, GradeScale scale, CancellationToken ct)
    {
        // 척도 라벨 = 나이스 드롭다운 텍스트 (항상 동일하게 운용)
        var neisText = task.TargetGrade;
        string why = "";

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var combo = await EnsureRowVisibleAsync(grid, task.RowIndex, ct);
            if (combo is null) return (false, "행을 화면에 못 띄움");

            try
            {
                var cur = await ComboBoxDriver.ReadValueAsync(combo);
                if (cur == neisText) return (true, "이미 설정됨");   // 멱등성

                var pick = await _combo!.OpenAndPickAsync(combo, neisText);
                if (pick.Ok)
                {
                    // ★ 재검증: 콤보를 fresh 로 다시 읽어 목표값 확인
                    var combo2 = await GetFreshComboAsync(grid, task.RowIndex);
                    var val = combo2 is null ? "" : await ComboBoxDriver.ReadValueAsync(combo2);
                    if (val == neisText) return (true, "");
                    why = $"검증 불일치('{val}')";
                }
                else why = pick.Reason;
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name;
            }

            if (attempt == 1) await Task.Delay(Timings.RetryDelay, ct);
        }
        return (false, why);
    }

    private async Task<ILocator?> GetFreshComboAsync(ILocator grid, int idx)
    {
        var loc = grid.Locator(
            $"div.cl-grid-row[data-rowindex='{idx}'] div[role='gridcell'][data-cellindex='6'] [role='combobox']");
        if (await loc.CountAsync() == 0) return null;
        var first = loc.First;
        return await first.IsVisibleAsync() ? first : null;
    }

    private async Task<ILocator?> EnsureRowVisibleAsync(ILocator grid, int idx, CancellationToken ct)
    {
        var combo = await GetFreshComboAsync(grid, idx);
        if (combo is null)
        {
            var vs = await _scroller!.GetVScrollAsync(grid);
            if (vs is { } v)
            {
                // 예상 위치로 스크롤
                var expectedAttr = await grid.GetAttributeAsync("aria-rowcount") ?? "1";
                int expected = Math.Max(int.Parse(expectedAttr) - 1, 1);
                double approx = v.scrollHeight * idx / expected - v.clientHeight / 2;
                await _scroller.ScrollProxyAsync(v.bar, Math.Max(approx, 0));
                await Task.Delay(Timings.AfterScroll, ct);
                combo = await GetFreshComboAsync(grid, idx);
            }
        }
        if (combo is null)   // 끝행 대비 (§8): CLX 공식 reveal API 로 행 직접 가시화
        {
            var expectedAttr2 = await grid.GetAttributeAsync("aria-rowcount") ?? "1";
            int expected2 = Math.Max(int.Parse(expectedAttr2) - 1, 1);
            await ClxGridApi.RevealAsync(_page!, expected2, idx);
            await Task.Delay(Timings.AfterScroll, ct);
            combo = await GetFreshComboAsync(grid, idx);
        }
        if (combo is not null)
        {
            await combo.ScrollIntoViewIfNeededAsync();
            await Task.Delay(Timings.AfterScrollIntoView, ct);
            combo = await GetFreshComboAsync(grid, idx);
        }
        return combo;
    }

    private static async Task<ILocator?> FindGridAsync(IPage page)
    {
        var grids = page.Locator(NeisSelectors.Grid);
        int n = await grids.CountAsync();
        for (int i = 0; i < n; i++)
            if (await grids.Nth(i).IsVisibleAsync()) return grids.Nth(i);
        return null;
    }

    public async Task<string> InspectDomAsync(CancellationToken ct = default)
    {
        var page = RequirePage();
        return await new DomInspector(page).InspectAsync();
    }

    public async Task<NarrativeReport> RunNarrativesAsync(
        string subjectName,
        IReadOnlyList<NarrativeEntry> entries,
        bool dryRun,
        int maxBytes,
        IProgress<ProgressInfo> progress,
        CancellationToken ct = default)
    {
        var page = RequirePage();
        void Log(string m) => progress.Report(new ProgressInfo(m));

        // 안전장치: 과목 콤보가 있으면 반드시 일치해야 함. 없으면(화면 구조 미확정) 경고 후 진행.
        var subject = await GetCurrentSubjectAsync(ct);
        if (subject is not null && subject != subjectName)
            throw new InvalidOperationException(
                $"화면 교과 '{subject}' ≠ 대상 '{subjectName}'. 나이스에서 '{subjectName}'를 조회하세요.");
        if (subject is null)
            Log($"⚠ 화면에서 교과 콤보를 찾지 못했습니다. 현재 화면이 '{subjectName}' 대상인지 직접 확인하세요.");

        var writer = new NarrativeWriter(page, _scroller!);
        return await writer.RunAsync(entries, dryRun, maxBytes, Log,
            (i, t) => progress.Report(new ProgressInfo("", i, t)), ct);
    }

    private IPage RequirePage() =>
        _page ?? throw new InvalidOperationException("브라우저에 연결되지 않았습니다. 먼저 연결하세요.");

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    }
}
