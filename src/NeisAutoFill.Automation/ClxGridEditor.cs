using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>칸 하나에 넣을 값.</summary>
/// <param name="Row">행 번호 (0부터)</param>
/// <param name="Column">열 번호 (0부터)</param>
/// <param name="Value">넣을 글</param>
/// <param name="FromList">콤보 칸인가 — 자유 입력이 아니라 <b>목록에서 고르는</b> 칸</param>
public sealed record CellEdit(int Row, int Column, string Value, bool FromList = false);

/// <summary>칸 하나가 목표와 다르다.</summary>
/// <param name="Row">행</param>
/// <param name="Column">열</param>
/// <param name="Wanted">넣으려던 값</param>
/// <param name="Actual">실제로 들어간 값</param>
public sealed record CellMismatch(int Row, int Column, string Wanted, string Actual)
{
    public override string ToString() => $"{Row}행 {Column}열: '{Wanted}' 넣으려 했는데 '{Actual}'";
}

/// <summary>
/// eXBuilder6(CLX) 그리드에 값을 넣는다 — <b>평가계획 화면의 확정 규칙</b>을 한 곳에 가둔다.
///
/// 이 그리드는 확정 규칙이 특이하다(실측 2026-08-21, 화면 지도 §5):
/// <list type="number">
/// <item>글 칸은 <b>더블클릭</b>하면 <c>TEXTAREA</c> 가 열린다. 콤보 칸은 <b>한 번</b> 누르면 목록이 뜬다.</item>
/// <item><b>Enter·Tab·Escape 로는 확정되지 않는다.</b> Enter 는 줄바꿈, Escape 는 취소,
///       그리드 <b>바깥</b>을 눌러도 편집이 그냥 버려진다.</item>
/// <item>확정하려면 <b>다른 칸을 눌러야 한다.</b> 그런데 <b>그 눌린 칸이 비워진다</b> —
///       편집기가 하나뿐이라 자리를 옮기며 빈 채로 새로 열리는 탓으로 보인다.</item>
/// </list>
///
/// 그래서 <b>다음에 채울 칸을 눌러 가며 연쇄로</b> 채운다. 비워지는 칸은 어차피 바로 다음에 채운다.
/// 마지막 칸은 편집기에 남으므로 <b>[저장]이 확정한다</b> — 저장은 편집 중인 칸을 먼저 확정한 뒤 보낸다.
///
/// <b>단, 저장은 바뀐 것이 있을 때만 확정해 준다.</b> 값이 예전과 똑같으면 나이스가
/// <c>"변경된 데이터가 없어서 저장할 수 없습니다."</c> 로 <b>거절하고</b>, 그러면 마지막 칸이
/// 확정되지 않은 채 빈 칸으로 남는다(실측 2026-08-21). 화면만 그럴 뿐 저장된 값은 멀쩡하다.
/// 그러니 <b>대조는 화면이 아니라 재조회한 뒤에</b> 해야 한다.
///
/// <b>넣은 뒤에는 반드시 다시 읽어 대조한다</b>(<see cref="VerifyAsync"/>).
/// 그리드를 처음 건드릴 때 한 번은 편집모드 진입이 실패했다.
/// </summary>
public sealed class ClxGridEditor(IPage page, string headerWord)
{
    /// <summary>일이 끝나길 기다리는 <b>최대</b> 시간. 대개 훨씬 빨리 끝난다.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(700);

    /// <summary>다시 볼 때까지의 간격.</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// 이 머리글이 있는 그리드를 찾는 스크립트 조각.
    ///
    /// <b>대화상자가 열려 있으면 그 안을 먼저 본다.</b> 본문에도 같은 머리글의 그리드가 있어서
    /// (영역명관리 상자와 성취기준 본문 둘 다 "영역명" 열을 갖는다) 그냥 찾으면
    /// <b>상자 뒤의 본문 그리드</b>를 잡는다 — 행을 더해도 늘지 않아 한참 헤맸다(실측 2026-08-21).
    /// </summary>
    private const string FindJs = @"(h) => {
        const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
        const has = g => [...g.querySelectorAll('[role=columnheader]')]
                          .some(c => (c.innerText || '').replace(/\s/g, '').includes(h));
        const dlg = [...document.querySelectorAll('[role=dialog]')].filter(vis).pop();
        if (dlg) {
          const inside = [...dlg.querySelectorAll('div.cl-grid[role=grid]')].find(has);
          if (inside) return inside;
        }
        return [...document.querySelectorAll('div.cl-grid[role=grid]')].filter(vis).find(has);
      }";

    public async Task<int> RowCountAsync() => await page.EvaluateAsync<int>(
        $"(h) => {{ const g = ({FindJs})(h); return g ? g.querySelectorAll(\"div.cl-grid-row[data-rowindex]\").length : 0; }}",
        headerWord);

    /// <summary>
    /// 머리글로 열 번호를 찾는다. 없으면 -1.
    ///
    /// <b>열 번호를 못 박지 않는다.</b> 같은 값을 담은 열이라도 화면마다 자리가 다르다 —
    /// 성취기준관리에서는 영역명이 2열인데 성취기준(평가기준)관리에서는 1열이다(실측 2026-08-21).
    /// 문서 파싱에서 겪은 것과 같은 함정이다.
    /// </summary>
    public async Task<int> ColumnAsync(string word) => await page.EvaluateAsync<int>(
        $@"(a) => {{
          const g = ({FindJs})(a.h); if (!g) return -1;
          const hs = [...g.querySelectorAll('[role=columnheader]')];
          return hs.findIndex(c => (c.innerText || '').replace(/\s/g, '').includes(a.w));
        }}", new { h = headerWord, w = word });

    /// <summary>여러 머리글의 열 번호를 한 번에.</summary>
    public async Task<IReadOnlyDictionary<string, int>> ColumnsAsync(params string[] words)
    {
        var found = new Dictionary<string, int>();
        foreach (var w in words) found[w] = await ColumnAsync(w);

        return found;
    }

    /// <summary>칸의 지금 값.</summary>
    public async Task<string> CellAsync(int row, int column) => await page.EvaluateAsync<string>(
        $@"(a) => {{
          const g = ({FindJs})(a.h); if (!g) return '';
          const r = [...g.querySelectorAll('div.cl-grid-row[data-rowindex]')]
            .find(x => x.getAttribute('data-rowindex') == a.row);
          if (!r) return '';
          const c = [...r.querySelectorAll('div[role=gridcell]')][a.col];
          return c ? (c.innerText || '').trim() : '';
        }}", new { h = headerWord, row, col = column });

    /// <summary>칸의 화면 좌표 (가운데). 없으면 null.</summary>
    private async Task<float[]?> PointAsync(int row, int column) => await page.EvaluateAsync<float[]?>(
        $@"(a) => {{
          const g = ({FindJs})(a.h); if (!g) return null;
          const r = [...g.querySelectorAll('div.cl-grid-row[data-rowindex]')]
            .find(x => x.getAttribute('data-rowindex') == a.row);
          if (!r) return null;
          const c = [...r.querySelectorAll('div[role=gridcell]')][a.col];
          if (!c) return null;
          const q = c.getBoundingClientRect();
          if (q.width < 4 || q.height < 4) return null;
          return [q.x + q.width / 2, q.y + q.height / 2];
        }}", new { h = headerWord, row, col = column });

    /// <summary>
    /// 여러 칸을 <b>연쇄로</b> 채운다. 순서가 곧 안전장치다 —
    /// 확정하려고 누르는 칸이 비워지는데, 그 칸이 바로 <b>다음에 채울 칸</b>이기 때문이다.
    /// 마지막 칸은 편집기에 남으므로 <b>호출부가 [저장]으로 마무리해야 한다.</b>
    /// </summary>
    public async Task FillChainAsync(IReadOnlyList<CellEdit> edits, CancellationToken ct = default)
    {
        for (var i = 0; i < edits.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            // 스스로 확정된 칸(한 줄 입력·콤보)은 다음 칸을 누를 필요가 없다
            if (await WriteAsync(edits[i], ct)) continue;

            if (i + 1 >= edits.Count) continue;

            var next = await PointAsync(edits[i + 1].Row, edits[i + 1].Column);
            if (next is null) continue;

            await page.Mouse.ClickAsync(next[0], next[1]);

            // 방금 칸이 <b>확정된 것을 눈으로 보고</b> 넘어간다 — 클릭 성공은 확정 성공이 아니다
            var wrote = edits[i];
            await UntilAsync(async () => Same(await CellAsync(wrote.Row, wrote.Column), wrote.Value),
                Settle, ct);
        }
    }

    /// <returns>스스로 확정됐으면 true — 다음 칸을 눌러 줄 필요가 없다.</returns>
    private async Task<bool> WriteAsync(CellEdit edit, CancellationToken ct)
    {
        var at = await PointAsync(edit.Row, edit.Column)
            ?? throw new InvalidOperationException($"{edit.Row}행 {edit.Column}열을 화면에서 찾지 못했습니다.");

        if (edit.FromList)
        {
            // 콤보 칸은 <b>한 번만</b> 누른다 — 더블클릭하면 목록이 열렸다 닫힌다.
            // 그리고 <b>이미 열려 있으면 누르지 않는다</b>: 앞 칸을 확정하려고 누른 그 클릭이
            // 이미 목록을 열어 놨을 수 있고, 한 번 더 누르면 도로 닫힌다(실측 2026-08-21).
            if (!await ListOpenAsync())
            {
                await page.Mouse.ClickAsync(at[0], at[1]);
                await UntilAsync(ListOpenAsync, Settle, ct);   // 목록이 뜨면 바로
            }

            await PickAsync(edit.Value, ct);

            return true;   // 목록에서 고르면 그 자리에서 확정된다
        }

        await page.Mouse.DblClickAsync(at[0], at[1]);

        // 편집기가 열릴 때까지만 기다린다 — 열리자마자 넘어간다
        await UntilAsync(() => page.EvaluateAsync<bool>(
            "() => !!document.activeElement && ['INPUT','TEXTAREA'].includes(document.activeElement.tagName)"),
            Settle, ct);

        // <b>편집기 종류에 따라 확정 방법이 다르다</b>(실측 2026-08-21).
        //   INPUT    한 줄 칸(영역명 등) — Enter 가 확정한다
        //   TEXTAREA 여러 줄 칸(성취기준·평가결과) — Enter 는 줄바꿈이라 확정이 안 된다
        // 이걸 몰라 한 줄 칸을 연쇄로 채웠더니 마지막 줄이 빈 채로 남아
        // 나이스가 "영역명은 공백으로 입력할 수 없습니다" 로 저장을 거절했다.
        var oneLine = await page.EvaluateAsync<bool>(
            "() => document.activeElement && document.activeElement.tagName === 'INPUT'");

        await page.Keyboard.PressAsync("Control+a");
        await page.Keyboard.InsertTextAsync(edit.Value);

        // 편집기에 글자가 들어간 것을 확인하면 바로 넘어간다 (고정 420ms → 대개 한 번에 참)
        await UntilAsync(() => page.EvaluateAsync<bool>(
            "(v) => !!document.activeElement && (document.activeElement.value || '') === v", edit.Value),
            Settle, ct);

        if (!oneLine) return false;

        await page.Keyboard.PressAsync("Enter");
        await UntilAsync(async () => Same(await CellAsync(edit.Row, edit.Column), edit.Value), Settle, ct);

        return true;
    }

    /// <summary>
    /// 조건이 참이 될 때까지 <b>짧게 여러 번</b> 본다. 고정 대기 대신 쓴다.
    ///
    /// 시간표에서 배운 것을 그대로 옮겼다: 고정 700ms 를 깔면 칸마다 그만큼 버리는데
    /// 실제로는 대개 50~150ms 면 끝난다. <b>일이 끝난 순간 넘어가고</b>, 안 끝나면 그때 기다린다.
    /// </summary>
    private static async Task<bool> UntilAsync(
        Func<Task<bool>> done, TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < deadline)
        {
            if (await done()) return true;
            await Task.Delay(Poll, ct);
        }

        return false;
    }

    /// <summary>고를 수 있는 목록이 지금 떠 있는가.</summary>
    private async Task<bool> ListOpenAsync() => await page.EvaluateAsync<bool>(
        @"() => [...document.querySelectorAll('div.cl-combobox-item')]
            .some(e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; })");

    /// <summary>열린 목록에서 그 글자를 고른다.</summary>
    private async Task PickAsync(string option, CancellationToken ct)
    {
        var at = await page.EvaluateAsync<float[]?>(@"(t) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          const i = [...document.querySelectorAll('div.cl-combobox-item')].filter(vis)
            .find(e => (e.innerText || '').trim() === t);
          if (!i) return null;
          i.scrollIntoView({ block: 'nearest' });
          const r = i.getBoundingClientRect();
          return [r.x + r.width / 2, r.y + r.height / 2];
        }", option);

        if (at is null) throw new InvalidOperationException($"목록에서 '{option}' 을 찾지 못했습니다.");

        await page.Mouse.ClickAsync(at[0], at[1]);

        // 목록이 닫히면 고른 것이다
        await UntilAsync(async () => !await ListOpenAsync(), Settle, ct);
    }

    /// <summary>
    /// 넣은 값이 실제로 들어갔는지 대조한다. 다른 칸만 돌려준다 — 비어 있으면 다 맞은 것이다.
    /// <b>클릭 성공은 입력 성공이 아니다</b>(기술설계 E-006).
    /// </summary>
    public async Task<IReadOnlyList<CellMismatch>> VerifyAsync(IReadOnlyList<CellEdit> edits)
    {
        var wrong = new List<CellMismatch>();

        foreach (var e in edits)
        {
            var actual = await CellAsync(e.Row, e.Column);
            if (!Same(actual, e.Value)) wrong.Add(new CellMismatch(e.Row, e.Column, e.Value, actual));
        }

        return wrong;
    }

    /// <summary>화면 글자는 줄바꿈·연속 공백이 다르게 보일 수 있어 공백을 눌러 비교한다.</summary>
    private static bool Same(string a, string b) =>
        System.Text.RegularExpressions.Regex.Replace(a, @"\s+", " ").Trim() ==
        System.Text.RegularExpressions.Regex.Replace(b, @"\s+", " ").Trim();
}
