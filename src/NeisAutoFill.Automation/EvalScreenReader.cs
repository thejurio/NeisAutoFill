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
    public async Task<bool> GoToAsync(EvalStep step)
    {
        var name = step == EvalStep.Standards ? "성취기준관리" : "성취기준(평가기준)관리";

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
        await Task.Delay(Settle);

        return true;
    }

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
