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
    /// <summary>이 문구가 보이면 저장을 진행해도 되는 확인창이다.</summary>
    private static readonly string[] ConfirmPhrases = { "저장하시겠습니까" };

    /// <summary>저장이 끝났다는 알림 — 확인만 누르면 된다.</summary>
    private static readonly string[] CompletePhrases = { "저장되었습니다", "저장하였습니다", "처리되었습니다", "완료되었습니다" };

    public async Task<SaveResult> SaveAsync(CancellationToken ct = default)
    {
        var seen = new List<string>();

        var state = await ButtonStateAsync("저장");
        if (state == "not-found") return new(SaveOutcome.Failed, "[저장] 버튼을 찾지 못했습니다.", seen);
        if (state == "disabled") return new(SaveOutcome.NothingToSave, "바뀐 내용이 없습니다.", seen);

        await ClickByTextAsync("저장");
        await WaitDialogAsync(appear: true);

        // 확인창 → 완료창 순으로 최대 두 번까지만 처리한다. 그 뒤에도 뭔가 남으면 사람이 본다.
        for (var step = 0; step < 2; step++)
        {
            ct.ThrowIfCancellationRequested();

            var text = await DialogTextAsync();
            if (text is null) break;   // 더 이상 상자가 없다
            seen.Add(text);

            if (!ConfirmPhrases.Any(text.Contains) && !CompletePhrases.Any(text.Contains))
                return new(SaveOutcome.UnknownDialog,
                    $"모르는 대화상자라 멈췄습니다: {Trim(text)}", seen);

            if (!await ClickDialogButtonAsync("확인"))
                return new(SaveOutcome.UnknownDialog, $"[확인]을 찾지 못했습니다: {Trim(text)}", seen);

            // 다음 상자(완료 알림)가 뜨거나, 아무것도 안 남을 때까지만 기다린다.
            // 고정 1500ms 를 쓰면 주마다 3초씩 버린다.
            await WaitDialogAsync(appear: true);
        }

        var leftover = await DialogTextAsync();
        if (leftover is not null)
        {
            seen.Add(leftover);
            return new(SaveOutcome.UnknownDialog, $"대화상자가 남아 있습니다: {Trim(leftover)}", seen);
        }

        return new(SaveOutcome.Saved, "저장 확인창까지 통과했습니다. 재조회로 확인하세요.", seen);
    }

    private static string Trim(string s) => s.Length > 80 ? s[..80] + "…" : s;

    /// <summary>폴링 간격.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>대화상자를 기다리는 최대 시간. 저장은 서버를 다녀오므로 넉넉히 둔다.</summary>
    private static readonly TimeSpan DialogWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 대화상자가 뜨기를(또는 사라지기를) 기다린다.
    /// <b>고정 대기를 쓰지 않는다</b> — 실측 메뉴·화면 반응은 100~300ms 인데
    /// 1200~1500ms 를 기다리고 있었다(2026-08-20).
    /// </summary>
    private async Task WaitDialogAsync(bool appear)
    {
        var deadline = DateTime.UtcNow + DialogWait;
        while (DateTime.UtcNow < deadline)
        {
            var text = await DialogTextAsync();
            if (appear ? text is not null : text is null) return;
            await Task.Delay(PollInterval);
        }
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
