using Microsoft.Playwright;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Automation;

/// <summary>셀 입력 한 건의 결과 (기술설계 §14 실패 분류와 짝을 이룬다).</summary>
public enum CellWriteOutcome
{
    /// <summary>입력하고 재검증까지 성공.</summary>
    Written,
    /// <summary>문서에 없는 수업이라 지웠고, 빈 칸이 된 것까지 확인했다.</summary>
    Cleared,
    /// <summary>지울 것이 이미 없었다.</summary>
    AlreadyEmpty,
    /// <summary>이미 목표와 같아 건드리지 않음.</summary>
    AlreadyMatches,
    /// <summary>기존에 다른 값이 있어 중단 (덮어쓰기 허용 안 됨).</summary>
    ExistingValueConflict,
    /// <summary>메뉴가 열리지 않음 — 휴업일·학기 시작 전.</summary>
    CellUnavailable,
    /// <summary>목표 항목이 현재 메뉴에 없음.</summary>
    OptionNotFound,
    /// <summary>클릭 후 셀 값이 목표와 다름.</summary>
    VerificationFailed,
    /// <summary>화면 구조를 신뢰할 수 없어 중단 (날짜 불일치 등).</summary>
    Aborted,
    /// <summary>
    /// <b>목표가 아닌 다른 칸이 바뀌었다.</b> 재시도로 덮을 수 없다 — 사람이 봐야 한다.
    /// 다시 시도하면 목표는 고쳐지지만 잘못 건드린 칸은 그대로 남는다.
    /// </summary>
    CollateralDamage,
}

/// <param name="Outcome">결과</param>
/// <param name="Detail">사용자에게 보여 줄 설명</param>
/// <param name="CellTextAfter">입력 후 읽은 셀 값 (비식별 처리는 호출부 몫)</param>
public sealed record CellWriteResult(CellWriteOutcome Outcome, string Detail, string CellTextAfter = "")
{
    public bool Changed => Outcome is CellWriteOutcome.Written or CellWriteOutcome.Cleared;
}

/// <summary>
/// 시간표 셀에 수업을 입력한다 (기술설계 §12 셀 입력).
///
/// <b>저장은 하지 않는다.</b> 저장은 주 단위로 별도 승인 아래 수행한다(T8).
/// 안전 규칙:
/// <list type="bullet">
/// <item>클릭 직전에 <b>그 셀의 날짜를 다시 확인</b>한다 — 화면이 바뀌었으면 중단</item>
/// <item>기존 값이 다르면 허용 없이는 덮어쓰지 않는다(D-010)</item>
/// <item>목표는 안정 키로만 찾는다. 과목명만 같고 교사가 다른 항목을 고르지 않는다(D-006)</item>
/// <item>클릭 성공이 아니라 <b>셀 재독</b>으로 성공을 판정한다</item>
/// </list>
/// </summary>
public sealed class TimetableCellWriter(IPage page)
{
    private readonly TimetableReader _reader = new(page);

    /// <summary>값이 바뀌기를 기다리는 폴링 간격.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);

    /// <summary>값이 안 바뀌면 이만큼까지만 기다린다.</summary>
    private static readonly TimeSpan ValueWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 셀 하나에 목표 항목을 넣는다. 화면에 지금 떠 있는 주에 그 셀이 있어야 한다.
    /// </summary>
    /// <param name="cell">날짜+교시</param>
    /// <param name="targetStableKey">넣을 항목의 안정 키</param>
    /// <param name="allowOverwrite">기존 값이 달라도 덮어쓸지 — 기본은 금지</param>
    public async Task<CellWriteResult> WriteAsync(
        TimetableCell cell, string targetStableKey, bool allowOverwrite = false, CancellationToken ct = default)
    {
        // ① 지금 화면의 셀 지도를 다시 읽는다 (클릭 직전 확인 — 화면이 바뀌었을 수 있다)
        var snapshot = await _reader.ReadCurrentWeekAsync();
        if (!snapshot.Cells.TryGetValue(cell, out var currentText))
            return new(CellWriteOutcome.Aborted, $"{cell} 이 지금 화면의 주에 없습니다.");

        var currentKey = currentText.Length == 0 ? "" : TimetableMenuParser.Parse(currentText).StableKey;

        // ④ 셀 좌표 — 행은 교시 순서, 열은 요일. 날짜로 교차 확인한 뒤에만 클릭한다
        var (rowIndex, dayColumn) = Locate(cell, snapshot);
        if (rowIndex < 0)
            return new(CellWriteOutcome.Aborted, $"{cell} 의 화면 위치를 찾지 못했습니다.");

        var verifiedDate = await _reader.ReadCellDateAsync(rowIndex, dayColumn);
        if (verifiedDate != cell.Date)
            return new(CellWriteOutcome.Aborted,
                $"클릭하려는 자리의 날짜가 {verifiedDate:yyyy-MM-dd} 로 목표({cell.Date:yyyy-MM-dd})와 다릅니다. 중단합니다.");

        // ⑤ 메뉴를 열고 목표 항목을 찾는다
        var catalog = await _reader.ReadCatalogAsync(rowIndex, dayColumn, closeAfter: false);
        if (catalog is null)
            return new(CellWriteOutcome.CellUnavailable, "이 날은 메뉴가 열리지 않습니다 (휴업일·학기 시작 전).");

        var target = catalog.Find(targetStableKey);
        if (target is null)
        {
            await _reader.CloseMenuAsync();
            return new(CellWriteOutcome.OptionNotFound, "목표 과목·교사가 현재 메뉴에 없습니다.");
        }

        // ② 이미 목표와 같으면 건드리지 않는다 (멱등성) — 저장 전 표기 차이까지 감안해 비교한다
        if (Verify(currentText, target, catalog))
        {
            await _reader.CloseMenuAsync();
            return new(CellWriteOutcome.AlreadyMatches, "이미 목표와 같습니다.", currentText);
        }

        // ③ 기존에 다른 값이 있으면 중단 (D-010)
        if (currentKey.Length > 0 && !allowOverwrite)
        {
            await _reader.CloseMenuAsync();
            return new(CellWriteOutcome.ExistingValueConflict,
                "기존에 다른 수업이 들어 있습니다. 덮어쓰려면 확인이 필요합니다.", currentText);
        }

        // ⑥ 클릭 — 원문이 정확히 일치하는 항목만. 스크롤 밖이면 보이게 한 뒤 누른다
        var clicked = await ClickMenuItemAsync(target.RawText);
        if (!clicked)
        {
            await _reader.CloseMenuAsync();
            return new(CellWriteOutcome.OptionNotFound, "메뉴에서 목표 항목을 누르지 못했습니다.");
        }

        // ⑦ 셀 값이 바뀔 때까지 <b>기다리며 확인한다</b>. 클릭 성공은 입력 성공이 아니다.
        //
        // 고정 대기(1200ms)를 쓰면 칸마다 1초 넘게 버린다 — 실제로는 대개 100~200ms 면 바뀐다.
        // 값이 바뀐 순간 넘어가고, 안 바뀌면 그때 실패로 본다.
        var afterText = "";
        var deadline = DateTime.UtcNow + ValueWait;

        while (DateTime.UtcNow < deadline)
        {
            var after = await _reader.ReadCurrentWeekAsync();
            after.Cells.TryGetValue(cell, out afterText);
            afterText ??= "";

            // <b>옆 칸이 바뀌었으면 즉시 멈춘다.</b> 지우기에만 있던 검사를 넣기에도 둔다 —
            // 좌표가 미끄러져 다른 칸에 값이 들어가면, 재시도로 목표는 고쳐지지만
            // <b>잘못 건드린 칸은 그대로 남아 함께 저장된다</b>(지적 2026-08-22).
            if (Collateral(snapshot, after, cell) is { } hit)
            {
                await _reader.EnsureMenuClosedAsync();
                return new(CellWriteOutcome.CollateralDamage,
                    $"넣으려던 칸은 {cell.Period}교시인데 {hit.Period}교시가 바뀌었습니다. 저장하지 마세요.", afterText);
            }

            if (Verify(afterText, target, catalog))
            {
                // 값은 메뉴가 닫히기 <b>전에</b> 바뀐다. 열린 채 두면 다음 클릭이 막힌다.
                await _reader.EnsureMenuClosedAsync();
                return new(CellWriteOutcome.Written, "입력하고 값을 확인했습니다.", afterText);
            }

            await Task.Delay(PollInterval);
        }

        await _reader.EnsureMenuClosedAsync();
        return new(CellWriteOutcome.VerificationFailed,
            "입력 후 값이 목표와 다릅니다. 저장하지 마세요.", afterText);
    }

    /// <summary>
    /// 문서에 없는 수업을 <b>지운다</b> — 메뉴의 <c>[수업삭제]</c>.
    ///
    /// 실측(2026-08-21): 확인창 없이 그 칸만 즉시 비워지고, 다른 교시가 밀리지 않으며 메뉴도 스스로 닫힌다.
    /// 그래도 <b>비었는지 눈으로 확인한 뒤에만</b> 성공이라고 한다 — 클릭 성공은 삭제 성공이 아니다.
    /// </summary>
    public async Task<CellWriteResult> ClearAsync(TimetableCell cell, CancellationToken ct = default)
    {
        var snapshot = await _reader.ReadCurrentWeekAsync();
        if (!snapshot.Cells.TryGetValue(cell, out var currentText))
            return new(CellWriteOutcome.Aborted, $"{cell} 이 지금 화면의 주에 없습니다.");

        if (currentText.Length == 0)
            return new(CellWriteOutcome.AlreadyEmpty, "이미 비어 있습니다.");

        var (rowIndex, dayColumn) = Locate(cell, snapshot);
        if (rowIndex < 0)
            return new(CellWriteOutcome.Aborted, $"{cell} 의 화면 위치를 찾지 못했습니다.");

        var verifiedDate = await _reader.ReadCellDateAsync(rowIndex, dayColumn);
        if (verifiedDate != cell.Date)
            return new(CellWriteOutcome.Aborted,
                $"지우려는 자리의 날짜가 {verifiedDate:yyyy-MM-dd} 로 목표({cell.Date:yyyy-MM-dd})와 다릅니다. 중단합니다.");

        if (await _reader.ReadCatalogAsync(rowIndex, dayColumn, closeAfter: false) is null)
            return new(CellWriteOutcome.CellUnavailable, "이 칸은 메뉴가 열리지 않습니다.");

        if (!await ClickMenuItemAsync(ClearCommand))
        {
            await _reader.CloseMenuAsync();
            return new(CellWriteOutcome.OptionNotFound, $"메뉴에서 [{ClearCommand}]를 찾지 못했습니다.");
        }

        var deadline = DateTime.UtcNow + ValueWait;
        while (DateTime.UtcNow < deadline)
        {
            var after = await _reader.ReadCurrentWeekAsync();
            after.Cells.TryGetValue(cell, out var afterText);

            // <b>옆 칸이 하나라도 바뀌었으면 즉시 멈춘다.</b>
            // 가상 스크롤에서 좌표가 미끄러지면 메뉴가 다른 칸에 걸린다 —
            // 넣기는 값을 다시 보면 되지만 지우기는 그때 이미 늦다(실측 2026-08-21).
            if (Collateral(snapshot, after, cell) is { } hit)
            {
                await _reader.EnsureMenuClosedAsync();
                return new(CellWriteOutcome.CollateralDamage,
                    $"지우려던 칸은 {cell.Period}교시인데 {hit.Period}교시가 바뀌었습니다. 저장하지 마세요.", currentText);
            }

            if (string.IsNullOrEmpty(afterText))
            {
                await _reader.EnsureMenuClosedAsync();
                return new(CellWriteOutcome.Cleared, "지우고 빈 칸이 된 것을 확인했습니다.");
            }

            await Task.Delay(PollInterval, ct);
        }

        await _reader.EnsureMenuClosedAsync();
        return new(CellWriteOutcome.VerificationFailed, "지웠는데도 값이 남아 있습니다. 저장하지 마세요.", currentText);
    }

    /// <summary>목표 말고 다른 칸이 바뀌었으면 그 칸을 돌려준다 (없으면 null).</summary>
    private static TimetableCell? Collateral(
        TimetableGridSnapshot before, TimetableGridSnapshot after, TimetableCell target)
    {
        foreach (var (key, was) in before.Cells)
        {
            if (key == target) continue;
            after.Cells.TryGetValue(key, out var now);
            if ((now ?? "") != was) return key;
        }

        return null;
    }

    /// <summary>나이스 메뉴의 삭제 명령 이름. 그 칸 하나만 지운다 ([전체 수업삭제]는 절대 쓰지 않는다).</summary>
    private const string ClearCommand = "수업삭제";

    /// <summary>
    /// 입력 후 셀 값이 목표와 같은지 (기술설계 R-006).
    /// 저장 전 셀에는 계정이 빠져 나오므로, 정확 키가 어긋나면 <b>계정을 뺀 키</b>로 한 번 더 본다.
    /// 단 그 키가 카탈로그에서 유일할 때만 인정한다 — 동명이인을 얼버무리지 않기 위해서다.
    /// </summary>
    private static bool Verify(string afterText, NeisTimetableOption target, TimetableCatalog catalog)
    {
        if (afterText.Length == 0) return false;

        var actual = TimetableMenuParser.Parse(afterText);
        if (actual.StableKey == target.StableKey) return true;

        return actual.TeacherAccount.Length == 0
            && actual.LooseKey == target.LooseKey
            && catalog.FindLoose(target.LooseKey) is not null;
    }

    /// <summary>
    /// 셀의 화면 좌표. 행 = 교시 순서(1교시가 0행), 열 = <b>스냅샷이 알려준 실제 열</b>.
    /// 열을 요일로 계산하지 않는다 — 필드 접두사는 요일이 아니다(실측 2026-08-19).
    /// </summary>
    private static (int Row, int Col) Locate(TimetableCell cell, TimetableGridSnapshot snapshot)
    {
        var periods = snapshot.Cells.Keys.Select(c => c.Period).Distinct().OrderBy(p => p).ToList();
        var row = periods.IndexOf(cell.Period);
        var col = snapshot.ColumnOf(cell.Date);

        return row < 0 || col < 0 ? (-1, -1) : (row, col);
    }

    /// <summary>열린 메뉴에서 원문이 정확히 일치하는 항목을 누른다. 화면 밖이면 스크롤한 뒤 누른다.</summary>
    private async Task<bool> ClickMenuItemAsync(string rawText) =>
        await page.EvaluateAsync<bool>(@"(text) => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const cancel = [...document.querySelectorAll('[role=button], button, .cl-button')]
            .filter(b => (b.innerText || '').trim() === '취소' && vis(b)).pop();
          if (!cancel) return false;

          let host = cancel.parentElement;
          while (host && host.querySelectorAll('[role=button], button, .cl-button').length < 5)
            host = host.parentElement;
          if (!host) return false;

          const item = [...host.querySelectorAll('[role=button], button, .cl-button')]
            .find(b => (b.innerText || '').trim() === text);
          if (!item) return false;

          item.scrollIntoView({ block: 'nearest' });   // 메뉴 자체 스크롤
          item.click();
          return true;
        }", rawText);
}
