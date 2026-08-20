using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>저장 시도의 결과.</summary>
public enum SaveOutcome
{
    /// <summary>저장 확인창까지 통과했다. 실제로 남았는지는 재조회로 확인해야 한다.</summary>
    Saved,
    /// <summary>바뀐 것이 없어 저장할 필요가 없었다 (저장 버튼 비활성).</summary>
    NothingToSave,
    /// <summary>예상하지 못한 대화상자가 떠서 아무 버튼도 누르지 않고 멈췄다.</summary>
    UnknownDialog,
    /// <summary>저장 버튼을 찾지 못하는 등 진행할 수 없었다.</summary>
    Failed,
}

/// <param name="Dialogs">거쳐 간 대화상자 문구 — 진단·기록용</param>
public sealed record SaveResult(SaveOutcome Outcome, string Detail, IReadOnlyList<string> Dialogs);

/// <summary>
/// 시간표 화면의 [저장]을 누른다 (기술설계 §12 저장, 실기기검증 §6).
///
/// <b>대화상자에 확인 버튼이 있다는 이유로 누르지 않는다.</b> 본문을 읽어 아는 것일 때만 누르고,
/// 모르는 상자를 만나면 아무것도 누르지 않고 멈춘다 — 무엇이 저장될지 모르는 채 확정하면 안 된다.
///
/// 실측 확인창(2026-08-19): <c>"저장하시겠습니까?" [확인] [취소]</c>
/// </summary>
public sealed class TimetableSaver(IPage page)
{
    private readonly TimetableReader reader = new(page);

    /// <summary>이 문구가 보이면 저장을 진행해도 되는 확인창이다.</summary>
    private static readonly string[] ConfirmPhrases = { "저장하시겠습니까" };

    /// <summary>저장이 끝났다는 알림 — 확인만 누르면 된다.</summary>
    private static readonly string[] CompletePhrases = { "저장되었습니다", "저장하였습니다", "처리되었습니다", "완료되었습니다" };

    /// <summary>
    /// 바뀐 것이 없을 때 나이스가 띄우는 알림 — <c>"저장할 내역이 없습니다."</c>
    /// 실패가 아니다. 확인만 누르고 <see cref="SaveOutcome.NothingToSave"/> 로 끝낸다.
    /// 이걸 모르면 "모르는 대화상자"로 멈추고, 그 알림이 남아 다음 클릭까지 막는다(실측 2026-08-21).
    /// </summary>
    private static readonly string[] NothingPhrases = { "저장할 내역이 없습니다", "변경된 내역이 없습니다" };

    public async Task<SaveResult> SaveAsync(CancellationToken ct = default)
    {
        var seen = new List<string>();

        // 메뉴가 열려 있으면 [저장] 클릭이 막힌다
        await reader.EnsureMenuClosedAsync();

        var state = await ButtonStateAsync("저장");
        if (state == "not-found") return new(SaveOutcome.Failed, "[저장] 버튼을 찾지 못했습니다.", seen);
        if (state == "disabled") return new(SaveOutcome.NothingToSave, "바뀐 내용이 없습니다.", seen);

        await ClickByTextAsync("저장");

        // ── ① 확인창 ─────────────────────────────────────────
        var first = await WaitForDialogAsync(previous: null);
        if (first is null)
            return new(SaveOutcome.Failed, "저장 확인창이 뜨지 않았습니다.", seen);

        seen.Add(first);

        if (CompletePhrases.Any(first.Contains))
        {
            // 확인 없이 바로 완료 알림이 뜨는 화면도 있을 수 있다
            return await AcknowledgeAsync(first, seen);
        }

        if (NothingPhrases.Any(first.Contains))
        {
            await AcknowledgeAsync(first, seen);
            return new(SaveOutcome.NothingToSave, "바뀐 것이 없어 저장하지 않았습니다.", seen);
        }

        if (!ConfirmPhrases.Any(first.Contains))
            return new(SaveOutcome.UnknownDialog, $"모르는 대화상자라 멈췄습니다: {Trim(first)}", seen);

        if (!await ClickDialogButtonAsync("확인"))
            return new(SaveOutcome.UnknownDialog, $"[확인]을 찾지 못했습니다: {Trim(first)}", seen);

        // ── ② 완료 알림 ───────────────────────────────────────
        //
        // <b>확인을 누른 직후에는 아무 상자도 없다.</b> 저장은 서버를 다녀오므로
        // 완료 알림이 1초 남짓 뒤에 뜬다. "상자가 없다 = 저장 끝" 으로 보고 나가면
        // 그 알림이 나중에 떠서 화면을 막고, 주차 이동이 안 된다(실측 2026-08-20).
        // 그래서 <b>완료 알림이 실제로 뜰 때까지</b> 기다린다.
        var done = await WaitForDialogAsync(previous: first);
        if (done is null)
            return new(SaveOutcome.UnknownDialog,
                "저장 완료 알림을 확인하지 못했습니다. 나이스 화면을 확인하세요.", seen);

        seen.Add(done);

        if (NothingPhrases.Any(done.Contains))
        {
            await AcknowledgeAsync(done, seen);
            return new(SaveOutcome.NothingToSave, "바뀐 것이 없어 저장하지 않았습니다.", seen);
        }

        if (!CompletePhrases.Any(done.Contains))
            return new(SaveOutcome.UnknownDialog, $"모르는 대화상자라 멈췄습니다: {Trim(done)}", seen);

        return await AcknowledgeAsync(done, seen);
    }

    /// <summary>완료 알림의 [확인]을 누르고, 상자가 모두 사라진 것까지 본다.</summary>
    private async Task<SaveResult> AcknowledgeAsync(string text, List<string> seen)
    {
        if (!await ClickDialogButtonAsync("확인"))
            return new(SaveOutcome.UnknownDialog, $"[확인]을 찾지 못했습니다: {Trim(text)}", seen);

        if (!await WaitGoneAsync())
        {
            var leftover = await DialogTextAsync();
            return new(SaveOutcome.UnknownDialog, $"대화상자가 남아 있습니다: {Trim(leftover ?? "")}", seen);
        }

        return new(SaveOutcome.Saved, "저장하고 알림까지 닫았습니다. 재조회로 확인하세요.", seen);
    }

    private static string Trim(string s) => s.Length > 80 ? s[..80] + "…" : s;

    /// <summary>폴링 간격.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>대화상자를 기다리는 최대 시간. 저장은 서버를 다녀오므로 넉넉히 둔다.</summary>
    private static readonly TimeSpan DialogWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 대화상자가 뜰 때까지 기다린다. <paramref name="previous"/> 와 같은 내용은 무시한다 —
    /// 방금 누른 상자가 아직 안 사라졌을 수 있고, 그것을 다시 붙잡으면 같은 상자를 두 번 처리한다.
    ///
    /// <b>중간에 상자가 하나도 없는 순간을 지나간다.</b> 저장은 서버를 다녀오므로
    /// 확인창이 사라지고 완료 알림이 뜨기까지 빈 시간이 있다. 그 틈에 나가면 안 된다.
    /// </summary>
    private async Task<string?> WaitForDialogAsync(string? previous)
    {
        var deadline = DateTime.UtcNow + DialogWait;
        while (DateTime.UtcNow < deadline)
        {
            var text = await DialogTextAsync();
            if (text is not null && text != previous) return text;
            await Task.Delay(PollInterval);
        }
        return null;
    }

    /// <summary>상자가 모두 사라질 때까지 기다린다.</summary>
    private async Task<bool> WaitGoneAsync()
    {
        var deadline = DateTime.UtcNow + DialogWait;
        while (DateTime.UtcNow < deadline)
        {
            if (await DialogTextAsync() is null) return true;
            await Task.Delay(PollInterval);
        }
        return false;
    }

    private async Task<string> ButtonStateAsync(string label) =>
        await page.EvaluateAsync<string>(@"(label) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const b = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(vis).find(x => (x.innerText || '').trim() === label);
          if (!b) return 'not-found';
          return b.className.includes('cl-disabled') || b.getAttribute('aria-disabled') === 'true'
              ? 'disabled' : 'enabled';
        }", label);

    private async Task ClickByTextAsync(string label) =>
        await page.EvaluateAsync(@"(label) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const b = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(vis).find(x => (x.innerText || '').trim() === label);
          if (b) b.click();
        }", label);

    /// <summary>지금 떠 있는 대화상자의 본문. 없으면 null.</summary>
    private async Task<string?> DialogTextAsync() =>
        await page.EvaluateAsync<string?>(@"() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          let best = null, bestArea = Infinity;
          [...document.querySelectorAll('div')].forEach(e => {
            if (!vis(e)) return;
            const r = e.getBoundingClientRect();
            const btns = [...e.querySelectorAll('[role=button], button, .cl-button')].filter(vis);
            if (r.width > 200 && r.width < 800 && r.height > 60 && r.height < 400 && r.top > 60
                && btns.length >= 1 && btns.length <= 4) {
              const area = r.width * r.height;   // 가장 안쪽(작은) 상자가 본문이다
              if (area < bestArea) { bestArea = area; best = e; }
            }
          });
          return best ? (best.innerText || '').trim().replace(/\n+/g, ' ') : null;
        }");

    /// <summary>
    /// 대화상자 안의 버튼을 <b>실제 마우스로</b> 누른다.
    /// 이 확인창 버튼은 <c>element.click()</c> 으로는 반응하지 않았다(2026-08-19 실측) —
    /// 좌표를 얻어 진짜 클릭을 보낸다. 화면 뒤쪽의 같은 이름 버튼을 건드리지 않도록
    /// 대화상자 크기의 조상을 가진 것만 고른다.
    /// </summary>
    private async Task<bool> ClickDialogButtonAsync(string label)
    {
        var point = await page.EvaluateAsync<double[]?>(@"(label) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const btn = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(vis)
            .filter(b => (b.innerText || '').trim() === label)
            .filter(b => {
              for (let e = b.parentElement; e; e = e.parentElement) {
                const r = e.getBoundingClientRect();
                if (r.width > 200 && r.width < 800 && r.height > 60 && r.height < 400) return true;
              }
              return false;
            })
            .pop();
          if (!btn) return null;
          const r = btn.getBoundingClientRect();
          return [r.x + r.width / 2, r.y + r.height / 2];
        }", label);

        if (point is null || point.Length < 2) return false;

        await page.Mouse.ClickAsync((float)point[0], (float)point[1]);
        return true;
    }
}
