using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>평가계획(안)관리 화면의 두 단계 (화면 지도 §1).</summary>
public enum EvalStep
{
    /// <summary>영역·성취기준·평가요소를 넣는 단계.</summary>
    Standards,

    /// <summary>고른 성취기준에 단계별 평가기준을 넣는 단계.</summary>
    Criteria,
}

/// <summary>조회 조건 한 벌.</summary>
/// <param name="SchoolYear">학년도</param>
/// <param name="Semester">학기</param>
/// <param name="Grade">학년</param>
/// <param name="Subject">교과(목)</param>
public sealed record EvalScope(string SchoolYear, string Semester, string Grade, string Subject)
{
    public override string ToString() => $"{SchoolYear}학년도 {Semester}학기 {Grade}학년 {Subject}";
}

/// <summary>
/// 평가계획(안)관리 화면을 <b>읽는다</b>. 넣거나 저장하지 않는다.
///
/// 화면 생김새는 <c>docs/maintenance/평가계획_나이스_화면지도.md</c> 에 실측해 두었다.
/// 조회를 누르기 전에는 편집 단추가 <b>전부 꺼져 있으므로</b>, 무엇을 하든 조회가 먼저다.
/// </summary>
public sealed class EvalScreenReader(IPage page)
{
    /// <summary>
    /// 단추를 누른 뒤 <b>클릭이 먹히도록</b> 두는 짧은 틈. 진짜 기다림은 폴링이 맡는다
    /// (조회는 그리드가 잠잠해질 때까지, 저장은 상자가 뜰 때까지).
    /// 예전 900ms 는 교과·단계마다 여러 번 걸려 그 자체로 수십 초를 먹었다.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan QueryWait = TimeSpan.FromSeconds(4);

    /// <summary>지금 평가계획(안)관리 화면인가.</summary>
    public async Task<bool> IsEvalScreenAsync() => (await ScreenTitleAsync()).Contains("평가계획");

    public async Task<string> ScreenTitleAsync() => await page.EvaluateAsync<string>(
        @"() => [...document.querySelectorAll('div.app-tit')]
            .filter(e => e.getBoundingClientRect().width > 0)
            .map(e => (e.innerText || '').trim()).join(' | ')");

    /// <summary>
    /// 조회 조건. <b>학년도와 학년의 aria-label 이 둘 다 "학년" 으로 시작한다</b> —
    /// 이름으로는 못 가리고 <b>화면에 놓인 순서</b>로 가린다(실측 2026-08-21).
    /// </summary>
    public async Task<EvalScope?> ReadScopeAsync()
    {
        var values = await ComboValuesAsync();

        return values.Count < 4 ? null : new EvalScope(values[0], values[1], values[2], values[3]);
    }

    private async Task<List<string>> ComboValuesAsync() =>
        (await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll(""div[role='combobox']"")]
                .filter(c => c.getBoundingClientRect().width > 2)
                .map(c => (c.innerText || '').trim())")).ToList();

    /// <summary>교과(목) 콤보가 고를 수 있는 목록 — 이 학년에 개설된 교과다.</summary>
    public async Task<IReadOnlyList<string>> ReadSubjectsAsync() => await ComboOptionsAsync("교과");

    /// <summary>단계선택 콤보의 후보. 실측: 2·3·4·5·7단계.</summary>
    public async Task<IReadOnlyList<string>> ReadLevelChoicesAsync() =>
        await ComboOptionsAsync("단계선택");

    private async Task<IReadOnlyList<string>> ComboOptionsAsync(string label)
    {
        var at = await ComboPointAsync(label);
        if (at is null) return Array.Empty<string>();

        await page.Mouse.ClickAsync(at[0], at[1]);
        await Task.Delay(Settle);

        var items = await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('div.cl-combobox-item')]
                .filter(e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; })
                .map(e => (e.innerText || '').trim()).filter(t => t.length > 0)");

        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(300);

        return items;
    }

    private async Task<float[]?> ComboPointAsync(string label) => await page.EvaluateAsync<float[]?>(
        @"(lab) => {
          const c = [...document.querySelectorAll(""div[role='combobox']"")]
            .filter(x => x.getBoundingClientRect().width > 2)
            .find(x => (x.getAttribute('aria-label') || '').startsWith(lab));
          if (!c) return null;
          const r = c.getBoundingClientRect();
          return [r.x + r.width - 12, r.y + r.height / 2];
        }", label);

    /// <summary>단계를 바꾼다 (화면 위쪽 두 카드). 바뀌면 편집 단추가 통째로 달라진다.</summary>
    public async Task<bool> GoToAsync(EvalStep step, CancellationToken ct = default)
    {
        var name = step == EvalStep.Standards ? "성취기준관리" : "성취기준(평가기준)관리";

        // <b>누른 것과 옮겨진 것은 다르다.</b> 예전엔 카드를 누르고 150ms 뒤 무조건 성공이라 했는데,
        // 실제로 화면이 안 바뀐 채 다음 걸음으로 가서 <b>1단계 화면에서 [단계선택]을 찾다</b> 멈췄다
        // (실측 2026-08-22). 이제 <b>그 단계에만 있는 것이 보일 때까지</b> 확인하고, 안 되면 다시 누른다.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await AtStepAsync(step)) return true;

            var at = await page.EvaluateAsync<float[]?>(@"(t) => {
              const vis = e => { const r = e.getBoundingClientRect(); return r.width > 150 && r.height > 24; };
              const x = [...document.querySelectorAll('div')].filter(vis)
                .filter(e => (e.innerText || '').trim() === t)
                .sort((a, b) => a.getBoundingClientRect().width - b.getBoundingClientRect().width)[0];
              if (!x) return null;
              const r = x.getBoundingClientRect();
              return [r.x + r.width / 2, r.y + r.height / 2];
            }", name);

            if (at is null) return false;

            await page.Mouse.ClickAsync(at[0], at[1]);

            if (await Wait.UntilAsync(() => AtStepAsync(step), QueryWait, ct)) return true;
        }

        return false;
    }

    /// <summary>
    /// 지금 그 단계 화면인가 — <b>그 단계에만 있는 것</b>으로 가린다.
    /// 1단계에는 [영역명관리]·[일괄업로드]가 있고, 2단계에는 [단계선택] 콤보가 있다.
    /// </summary>
    private async Task<bool> AtStepAsync(EvalStep step) => step == EvalStep.Criteria
        ? await page.EvaluateAsync<bool>(
            @"() => [...document.querySelectorAll(""div[role='combobox']"")]
                .filter(x => x.getBoundingClientRect().width > 2)
                .some(x => (x.getAttribute('aria-label') || '').startsWith('단계선택'))")
        : await page.EvaluateAsync<bool>(
            @"() => [...document.querySelectorAll('[role=button], button, .cl-button')]
                .filter(e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; })
                .some(e => (e.innerText || '').trim() === '영역명관리')");

    /// <summary>
    /// 조회를 누른다. 이걸 해야 편집 단추가 켜진다.
    ///
    /// <b>고정 대기를 쓰지 않는다.</b> 예전엔 무조건 4초를 깔았는데, 교과마다 단계마다 부르는 자리라
    /// 그것만으로 1분 넘게 버렸다. 지금은 <b>그리드가 조용해질 때까지</b>만 본다 —
    /// 줄 수가 두 번 연달아 같으면 다 받은 것으로 본다(시간표에서 쓴 것과 같은 방식).
    /// </summary>
    public async Task<bool> QueryAsync(CancellationToken ct = default)
    {
        if (!await ClickAsync("조회")) return false;

        await SettledAsync(ct);

        return true;
    }

    /// <summary>그리드 줄 수가 잠잠해질 때까지. 오래 걸리면 <see cref="QueryWait"/> 에서 끊는다.</summary>
    private async Task SettledAsync(CancellationToken ct)
    {
        const string Count = "() => [...document.querySelectorAll('div.cl-grid[role=grid]')]"
                             + ".reduce((n, g) => n + g.querySelectorAll('div.cl-grid-row[data-rowindex]').length, 0)";

        var deadline = DateTime.UtcNow + QueryWait;
        var last = -1;
        var stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(30, ct);

            var now = await page.EvaluateAsync<int>(Count);
            stable = now == last ? stable + 1 : 0;
            last = now;

            if (stable >= 2) return;   // 두 번 연달아 같으면 다 받았다 (최소 ~90ms)
        }
    }

    /// <summary>글자로 단추를 누른다. 꺼져 있으면 누르지 않고 false 를 돌려준다.</summary>
    public async Task<bool> ClickAsync(string text)
    {
        await ClearAlertsAsync();   // 남은 알림이 화면을 막고 있으면 무엇을 눌러도 안 먹는다

        var at = await page.EvaluateAsync<float[]?>(@"(t) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          const x = [...document.querySelectorAll('[role=button], button, .cl-button')].filter(vis)
            .find(e => (e.innerText || '').trim() === t);
          if (!x) return null;
          if (x.getAttribute('aria-disabled') === 'true' ||
              (x.className || '').includes('cl-disabled')) return null;
          const r = x.getBoundingClientRect();
          return [r.x + r.width / 2, r.y + r.height / 2];
        }", text);

        if (at is null) return false;

        await page.Mouse.ClickAsync(at[0], at[1]);
        await Task.Delay(Settle);

        return true;
    }

    /// <summary>지금 켜져 있는 단추들 — 어느 단계인지, 조회를 했는지 여기서 알 수 있다.</summary>
    public async Task<IReadOnlyList<string>> EnabledButtonsAsync() =>
        await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('[role=button], button, .cl-button')]
                .filter(e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; })
                .filter(e => e.getAttribute('aria-disabled') !== 'true' &&
                             !(e.className || '').includes('cl-disabled'))
                .map(e => (e.innerText || '').trim()).filter(t => t.length > 0)");

    /// <summary>성취기준 그리드 (영역명·성취기준·평가요소).</summary>
    public ClxGridEditor Standards => new(page, "성취기준");

    /// <summary>평가기준 그리드 (평가단계·평가결과).</summary>
    public ClxGridEditor Criteria => new(page, "평가단계");

    /// <summary>
    /// 왼쪽 성취기준 그리드에서 한 줄을 고른다 — 골라야 오른쪽 평가기준이 살아난다.
    /// </summary>
    public async Task<bool> SelectStandardAsync(int row)
    {
        await ClearAlertsAsync();   // 남은 알림이 그리드를 막고 있으면 줄이 안 골라진다

        var at = await page.EvaluateAsync<float[]?>(@"(row) => {
          const g = [...document.querySelectorAll('div.cl-grid[role=grid]')]
            .find(x => [...x.querySelectorAll('[role=columnheader]')]
                        .some(c => (c.innerText || '').includes('영역명')));
          if (!g) return null;
          const r = [...g.querySelectorAll('div.cl-grid-row[data-rowindex]')]
            .find(x => x.getAttribute('data-rowindex') == row);
          if (!r) return null;
          const c = [...r.querySelectorAll('div[role=gridcell]')][1];
          if (!c) return null;
          const q = c.getBoundingClientRect();
          return [q.x + q.width / 2, q.y + q.height / 2];
        }", row);

        if (at is null) return false;

        await page.Mouse.ClickAsync(at[0], at[1]);
        await Task.Delay(Settle);

        return true;
    }

    /// <summary>
    /// 보이는 대화상자의 글. 없으면 null.
    /// 저장 확인·완료, "변경사항이 반영되지 않았습니다" 따위가 여기로 온다.
    /// </summary>
    public async Task<string?> DialogTextAsync() => await page.EvaluateAsync<string?>(
        @"() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          const d = [...document.querySelectorAll('[role=dialog]')].filter(vis).pop();
          return d ? (d.innerText || '').replace(/\s+/g, ' ').trim() : null;
        }");

    /// <summary>
    /// <b>알림 상자</b>만 고르는 자바스크립트. 작업 창(일괄업로드 등)과 구분한다.
    ///
    /// 나이스는 <b>작업 창도 <c>role=dialog</c></b> 로 그린다. 그래서 "떠 있는 마지막 dialog" 를
    /// 알림으로 보면 <b>업로드 창 자신을 알림으로 착각</b>한다 — 실제로 그래서 저장 확인을 못 누르고
    /// "모르는 대화상자"로 멈췄다(실측 2026-08-22).
    ///
    /// 알림은 <b>그리드가 없고</b> 확인·예 같은 단추를 가진 작은 상자다. 그것만 고른다.
    /// </summary>
    private const string AlertsJs = """
        () => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          const ok = ['확인', '예', '아니오', '취소'];
          return [...document.querySelectorAll('[role=dialog]')].filter(vis)
            .filter(d => !d.querySelector('[role=grid]'))
            .filter(d => [...d.querySelectorAll('[role=button], button, .cl-button')]
              .some(b => ok.includes((b.innerText || '').trim())));
        }
        """;

    /// <summary>지금 떠 있는 알림 상자의 글. 없으면 null.</summary>
    public async Task<string?> AlertTextAsync() => await page.EvaluateAsync<string?>(
        $$"""
        () => {
          const ds = ({{AlertsJs}})();
          const d = ds[ds.length - 1];
          return d ? (d.innerText || '').replace(/\s+/g, ' ').trim() : null;
        }
        """);

    /// <summary>
    /// 알림 상자의 단추를 누르고 <b>닫힌 것을 확인</b>한다. 못 닫으면 false.
    ///
    /// <b>누른 것과 닫힌 것은 다르다.</b> 한 번 눌러 보고 넘어갔더니 알림이 남은 채로 다음 걸음을 갔고,
    /// 그 알림이 화면을 막아 <c>[단계선택]에서 3단계를 고르지 못했습니다</c> 로 멈췄다(실측 2026-08-22).
    /// 상자는 뜨면서 자리가 잡히기도 해서 <b>좌표를 다시 재며 여러 번</b> 눌러 본다.
    /// </summary>
    public async Task<bool> ClickAlertAsync(string text)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var before = await AlertTextAsync();
            if (before is null) return true;   // 이미 닫혔다

            if (!await PressAlertAsync(text)) return false;

            if (await Wait.UntilAsync(async () => await AlertTextAsync() != before,
                    TimeSpan.FromMilliseconds(1200)))
                return true;
        }

        return false;
    }

    /// <summary>알림 단추를 한 번 누른다. 못 찾으면 false.</summary>
    private async Task<bool> PressAlertAsync(string text)
    {
        var at = await page.EvaluateAsync<float[]?>(
            $$"""
            (t) => {
              const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
              const ds = ({{AlertsJs}})();
              const d = ds[ds.length - 1];
              if (!d) return null;
              const x = [...d.querySelectorAll('[role=button], button, .cl-button')].filter(vis)
                .find(e => (e.innerText || '').trim() === t);
              if (!x) return null;
              const r = x.getBoundingClientRect();
              return [r.x + r.width / 2, r.y + r.height / 2];
            }
            """, text);

        if (at is null) return false;

        await page.Mouse.ClickAsync(at[0], at[1]);

        return true;
    }

    /// <summary>
    /// 떠 있는 알림을 모두 치운다. <b>무엇을 누르기 전에</b> 부른다 —
    /// 남은 알림이 화면을 막으면 그다음 클릭이 통째로 헛돈다.
    /// </summary>
    public async Task ClearAlertsAsync()
    {
        for (var i = 0; i < 3 && await AlertTextAsync() is not null; i++)
            if (!await ClickAlertAsync("확인")) return;
    }

    /// <summary>
    /// 알림이 뜨고 <b>글까지 다 채워질 때까지</b> 기다린다. 안 뜨면 null.
    ///
    /// <b>뜨자마자 읽으면 안 된다.</b> 상자는 제목·단추가 먼저 그려지고 본문이 나중에 채워진다 —
    /// 그 틈에 읽어서 <c>"알림 확인"</c> 만 얻고 "모르는 대화상자"로 멈춘 적이 있다(실측 2026-08-22).
    /// 그래서 <b>같은 글이 두 번 연달아 읽힐 때</b>까지 본다.
    /// </summary>
    public Task<string?> WaitForAlertAsync(TimeSpan limit, CancellationToken ct = default) =>
        Wait.SettledAsync(AlertTextAsync, limit, ct);

    /// <summary>대화상자가 뜰 때까지 기다린다. 떴으면 그 글, 안 뜨면 null.</summary>
    public async Task<string?> WaitForDialogAsync(TimeSpan limit, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < deadline)
        {
            if (await DialogTextAsync() is { } text) return text;
            await Task.Delay(30, ct);
        }

        return null;
    }

    /// <summary>대화상자의 단추를 누른다 (확인·취소·닫기). 진짜 마우스로 누른다.</summary>
    public async Task<bool> ClickDialogAsync(string text)
    {
        var at = await page.EvaluateAsync<float[]?>(@"(t) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          const d = [...document.querySelectorAll('[role=dialog]')].filter(vis).pop();
          if (!d) return null;
          const x = [...d.querySelectorAll('[role=button], button, .cl-button')].filter(vis)
            .find(e => (e.innerText || '').trim() === t);
          if (!x) return null;
          const r = x.getBoundingClientRect();
          return [r.x + r.width / 2, r.y + r.height / 2];
        }", text);

        if (at is null) return false;

        await page.Mouse.ClickAsync(at[0], at[1]);
        await Task.Delay(Settle);

        return true;
    }
}
