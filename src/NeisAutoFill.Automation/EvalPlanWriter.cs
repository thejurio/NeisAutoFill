using Microsoft.Playwright;
using NeisAutoFill.Core.Evaluation;

namespace NeisAutoFill.Automation;

/// <summary>저장 한 번의 결과.</summary>
public enum EvalSaveOutcome
{
    /// <summary>저장하고 완료 알림까지 확인했다.</summary>
    Saved,

    /// <summary>바뀐 것이 없어 나이스가 저장을 거절했다 — 실패가 아니다.</summary>
    NothingToSave,

    /// <summary>[저장]을 누를 수 없었다.</summary>
    CannotSave,

    /// <summary>모르는 대화상자가 떠서 멈췄다.</summary>
    UnknownDialog,
}

/// <param name="Outcome">결과</param>
/// <param name="Detail">사람에게 보여 줄 설명</param>
/// <param name="Dialogs">거쳐 간 대화상자 글 — 무슨 일이 있었는지 남긴다</param>
public sealed record EvalSaveResult(
    EvalSaveOutcome Outcome, string Detail, IReadOnlyList<string> Dialogs);

/// <summary>
/// 평가계획(안)관리 화면에 <b>실제로 넣고 저장한다</b>.
///
/// 순서가 강제된다(기술설계 E-001):
/// <list type="number">
/// <item><see cref="RegisterAreasAsync"/> — 영역명을 먼저 등록해야 성취기준에서 고를 수 있다</item>
/// <item><see cref="AddStandardsAsync"/> — 영역·성취기준·평가요소</item>
/// <item><see cref="WriteCriteriaAsync"/> — 고른 성취기준에 단계별 평가기준</item>
/// </list>
///
/// 넣는 방법의 까다로운 규칙은 <see cref="ClxGridEditor"/> 에 가둬 두었다.
/// </summary>
public sealed class EvalPlanWriter(IPage page, EvalScreenReader reader)
{
    /// <summary>단추 클릭이 먹히도록 두는 짧은 틈. 진짜 기다림은 폴링이 맡는다.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ServerWait = TimeSpan.FromSeconds(6);

    /// <summary>저장하겠느냐고 묻는 상자.</summary>
    private static readonly string[] Confirm = { "저장하시겠습니까" };

    /// <summary>저장이 끝났다는 상자.</summary>
    private static readonly string[] Done = { "저장이 완료되었습니다", "저장되었습니다", "처리되었습니다" };

    /// <summary>바뀐 것이 없다는 상자 — 실패가 아니다(화면 지도 §5).</summary>
    private static readonly string[] Nothing = { "변경된 데이터가 없", "저장할 내역이 없" };

    // ── 걸음 0. 영역명 등록 ────────────────────────────────

    /// <summary>
    /// [영역명관리]에 영역명을 등록한다. <b>이미 있는 것은 건너뛴다.</b>
    /// 성취기준 화면의 영역명은 콤보라, 여기 없는 이름은 고를 수가 없다(E-001).
    /// </summary>
    /// <returns>새로 등록한 이름들</returns>
    public async Task<IReadOnlyList<string>> RegisterAreasAsync(
        IEnumerable<string> names, CancellationToken ct = default)
    {
        if (!await reader.ClickAsync("영역명관리"))
            throw new InvalidOperationException("[영역명관리]를 누를 수 없습니다. 조회를 먼저 하세요.");

        await Task.Delay(Settle, ct);

        var grid = new ClxGridEditor(page, "영역명");
        var nameColumn = await grid.ColumnAsync("영역명");
        if (nameColumn < 0) throw new InvalidOperationException("영역명 열을 찾지 못했습니다.");

        var existing = new List<string>();
        for (var r = 0; r < await grid.RowCountAsync(); r++)
            existing.Add((await grid.CellAsync(r, nameColumn)).Trim());

        var wanted = names.Select(n => n.Trim()).Where(n => n.Length > 0).Distinct()
            .Where(n => !existing.Contains(n)).ToList();

        if (wanted.Count == 0)
        {
            await reader.ClickDialogAsync("닫기");
            return wanted;
        }

        // 행을 먼저 다 만들고, 한 번에 연쇄로 채운다
        var edits = new List<CellEdit>();
        foreach (var name in wanted)
        {
            await reader.ClickDialogAsync("행추가");
            edits.Add(new CellEdit(await grid.RowCountAsync() - 1, nameColumn, name));
        }

        await grid.FillChainAsync(edits, ct);

        var save = await SaveAsync(inDialog: true, ct);
        if (save.Outcome is not (EvalSaveOutcome.Saved or EvalSaveOutcome.NothingToSave))
            throw new InvalidOperationException($"영역명을 저장하지 못했습니다 — {save.Detail}");

        var wrong = await grid.VerifyAsync(edits);
        if (wrong.Count > 0)
            throw new InvalidOperationException($"영역명이 목표와 다릅니다 — {wrong[0]}");

        await reader.ClickDialogAsync("닫기");
        await Task.Delay(Settle, ct);

        return wanted;
    }

    // ── 걸음 1. 성취기준 ───────────────────────────────────

    /// <summary>
    /// 성취기준 줄을 더한다 (영역명 고르기 + 성취기준 + 평가요소). 저장까지 한다.
    /// <b>이미 같은 성취기준이 있으면 건너뛴다</b> — 나이스는 덮어쓰지 않고 <b>더하기만</b> 하기 때문이다(E-004).
    /// </summary>
    /// <returns>새로 넣은 건수</returns>
    public async Task<int> AddStandardsAsync(
        IReadOnlyList<(string Area, string Standard, string Element)> rows,
        CancellationToken ct = default)
    {
        var grid = reader.Standards;

        // 열 번호는 화면마다 다르다 — 머리글로 찾는다
        var areaColumn = await grid.ColumnAsync("영역명");
        var standardColumn = await grid.ColumnAsync("성취기준");
        var elementColumn = await grid.ColumnAsync("평가요소");
        if (areaColumn < 0 || standardColumn < 0 || elementColumn < 0)
            throw new InvalidOperationException("성취기준 그리드의 열을 찾지 못했습니다.");

        var already = new List<string>();
        for (var r = 0; r < await grid.RowCountAsync(); r++)
            already.Add(Squash(await grid.CellAsync(r, standardColumn)));

        var todo = rows.Where(x => !already.Contains(Squash(x.Standard))).ToList();
        if (todo.Count == 0) return 0;

        var edits = new List<CellEdit>();
        foreach (var (area, standard, element) in todo)
        {
            if (!await reader.ClickAsync("행추가"))
                throw new InvalidOperationException("[행추가]를 누를 수 없습니다.");

            var row = await grid.RowCountAsync() - 1;
            edits.Add(new CellEdit(row, areaColumn, area, FromList: true));
            edits.Add(new CellEdit(row, standardColumn, standard));
            edits.Add(new CellEdit(row, elementColumn, element));
        }

        await grid.FillChainAsync(edits, ct);

        // NothingToSave 도 성공이다 — 이미 같은 값이 들어 있으면 나이스가 저장을 거절한다.
        // 이걸 실패로 보면 <b>두 번째 실행부터 늘 멈춘다</b>(실측 2026-08-22).
        var save = await SaveAsync(inDialog: false, ct);
        if (save.Outcome is not (EvalSaveOutcome.Saved or EvalSaveOutcome.NothingToSave))
            throw new InvalidOperationException($"성취기준을 저장하지 못했습니다 — {save.Detail}");

        return todo.Count;
    }

    // ── 걸음 2. 평가기준 ───────────────────────────────────

    /// <summary>
    /// 고른 성취기준에 단계별 평가기준을 넣는다. 단계 수를 먼저 골라야 빈 줄이 생긴다.
    /// </summary>
    public async Task WriteCriteriaAsync(
        int standardRow, IReadOnlyList<EvalCriterion> criteria, CancellationToken ct = default)
    {
        var count = EvalLevelScale.Resolve(criteria.Count, out var why)
            ?? throw new InvalidOperationException(why);

        if (!await reader.SelectStandardAsync(standardRow))
            throw new InvalidOperationException($"{standardRow}번째 성취기준을 고르지 못했습니다.");

        if (!await PickLevelCountAsync(count, ct))
            throw new InvalidOperationException($"[단계선택]에서 {count}단계를 고르지 못했습니다.");

        // 단계를 고르면 그만큼 줄이 생긴다 — <b>줄이 나올 때까지만</b> 기다린다(고정 2초를 걷어냈다).
        var grid = reader.Criteria;
        var deadline = DateTime.UtcNow + ServerWait;
        while (await grid.RowCountAsync() < count && DateTime.UtcNow < deadline)
            await Task.Delay(60, ct);

        if (await grid.RowCountAsync() < count)
            throw new InvalidOperationException(
                $"평가기준 줄이 {await grid.RowCountAsync()}개뿐입니다 ({count}개가 나와야 합니다).");

        var levelColumn = await grid.ColumnAsync("평가단계");
        var resultColumn = await grid.ColumnAsync("평가결과");
        if (levelColumn < 0 || resultColumn < 0)
            throw new InvalidOperationException("평가기준 그리드의 열을 찾지 못했습니다.");

        var edits = new List<CellEdit>();
        for (var i = 0; i < count; i++)
        {
            edits.Add(new CellEdit(i, levelColumn, criteria[i].Level));
            edits.Add(new CellEdit(i, resultColumn, criteria[i].Result));
        }

        await grid.FillChainAsync(edits, ct);

        // 여기가 실제로 걸렸다 — 국어를 두 번째로 넣자 "바뀐 것이 없어 저장하지 않았습니다" 로
        // 멈췄다(실측 2026-08-22). 이미 들어가 있는 것은 다시 넣을 필요가 없을 뿐 실패가 아니다.
        var save = await SaveAsync(inDialog: false, ct);
        if (save.Outcome is not (EvalSaveOutcome.Saved or EvalSaveOutcome.NothingToSave))
            throw new InvalidOperationException($"평가기준을 저장하지 못했습니다 — {save.Detail}");
    }

    private async Task<bool> PickLevelCountAsync(int count, CancellationToken ct)
    {
        var at = await page.EvaluateAsync<float[]?>(@"() => {
          const c = [...document.querySelectorAll(""div[role='combobox']"")]
            .filter(x => x.getBoundingClientRect().width > 2)
            .find(x => (x.getAttribute('aria-label') || '').startsWith('단계선택'));
          if (!c) return null;
          const r = c.getBoundingClientRect();
          return [r.x + r.width - 12, r.y + r.height / 2];
        }");

        if (at is null) return false;

        await page.Mouse.ClickAsync(at[0], at[1]);
        await Task.Delay(Settle, ct);

        var item = await page.EvaluateAsync<float[]?>(@"(t) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 2 && r.height > 2; };
          const i = [...document.querySelectorAll('div.cl-combobox-item')].filter(vis)
            .find(e => (e.innerText || '').trim() === t);
          if (!i) return null;
          const r = i.getBoundingClientRect();
          return [r.x + r.width / 2, r.y + r.height / 2];
        }", EvalLevelScale.Label(count));

        if (item is null) return false;

        await page.Mouse.ClickAsync(item[0], item[1]);
        await Task.Delay(Settle, ct);

        return true;
    }

    // ── 저장 ───────────────────────────────────────────────

    /// <summary>
    /// [저장]을 누르고 <b>두 단계 대화상자</b>를 끝까지 처리한다 (화면 지도 §6).
    ///
    /// <c>저장하시겠습니까?</c> [확인] → <b>아무 상자도 없는 구간</b> → <c>저장이 완료되었습니다</c> [확인].
    /// 가운데 빈 구간을 성공으로 착각하면 안 된다 — 시간표에서 똑같이 당했다(실기기검증 §4-Q).
    ///
    /// <b>저장은 편집 중인 칸을 먼저 확정해 준다</b> — 연쇄 입력의 마지막 칸이 이때 들어간다.
    /// 다만 바뀐 것이 없으면 나이스가 거절하고, 그러면 그 칸은 확정되지 않는다.
    /// </summary>
    public async Task<EvalSaveResult> SaveAsync(bool inDialog, CancellationToken ct = default)
    {
        var seen = new List<string>();

        var clicked = inDialog
            ? await reader.ClickDialogAsync("저장")
            : await reader.ClickAsync("저장");

        if (!clicked) return new(EvalSaveOutcome.CannotSave, "[저장]을 누를 수 없습니다.", seen);

        var first = await WaitDialogAsync(null, ct);
        if (first is null)
            return new(EvalSaveOutcome.UnknownDialog, "저장 확인창이 뜨지 않았습니다.", seen);

        seen.Add(Trim(first));

        if (Nothing.Any(first.Contains))
        {
            await reader.ClickDialogAsync("확인");
            return new(EvalSaveOutcome.NothingToSave, "바뀐 것이 없어 저장하지 않았습니다.", seen);
        }

        if (Done.Any(first.Contains))
        {
            await reader.ClickDialogAsync("확인");
            return new(EvalSaveOutcome.Saved, "저장했습니다.", seen);
        }

        if (!Confirm.Any(first.Contains))
            return new(EvalSaveOutcome.UnknownDialog, $"모르는 대화상자입니다: {Trim(first)}", seen);

        await reader.ClickDialogAsync("확인");

        var done = await WaitDialogAsync(first, ct);
        if (done is null)
            return new(EvalSaveOutcome.UnknownDialog, "저장 완료 알림을 확인하지 못했습니다.", seen);

        seen.Add(Trim(done));

        if (Nothing.Any(done.Contains))
        {
            await reader.ClickDialogAsync("확인");
            return new(EvalSaveOutcome.NothingToSave, "바뀐 것이 없어 저장하지 않았습니다.", seen);
        }

        if (!Done.Any(done.Contains))
            return new(EvalSaveOutcome.UnknownDialog, $"모르는 대화상자입니다: {Trim(done)}", seen);

        await reader.ClickDialogAsync("확인");

        return new(EvalSaveOutcome.Saved, "저장하고 완료 알림까지 확인했습니다.", seen);
    }

    /// <summary>
    /// <paramref name="previous"/> 와 <b>글이 다른</b> 상자가 뜰 때까지 기다린다.
    /// 상자가 사라진 것을 끝으로 보면 안 된다 — 다음 상자가 아직 안 뜬 것일 수 있다.
    /// </summary>
    private async Task<string?> WaitDialogAsync(string? previous, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ServerWait;

        while (DateTime.UtcNow < deadline)
        {
            var text = await reader.DialogTextAsync();
            if (text is not null && text != previous) return text;

            await Task.Delay(80, ct);
        }

        return null;
    }

    private static string Trim(string s) => s.Length <= 90 ? s : s[..90] + "…";

    private static string Squash(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "").Trim();
}
