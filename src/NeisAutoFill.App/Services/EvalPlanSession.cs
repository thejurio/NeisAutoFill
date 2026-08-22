using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using System.IO;
using NeisAutoFill.Core.Evaluation;
using NeisAutoFill.Excel;

namespace NeisAutoFill.App.Services;

/// <summary>교과 하나를 넣은 결과.</summary>
/// <param name="Subject">교과명</param>
/// <param name="Areas">새로 등록한 영역명 수</param>
/// <param name="Standards">새로 넣은 성취기준 수</param>
/// <param name="Criteria">평가기준을 넣은 성취기준 수</param>
/// <param name="Failure">막힌 이유. null 이면 끝까지 갔다</param>
public sealed record EvalSubjectResult(
    string Subject, int Areas, int Standards, int Criteria, string? Failure = null)
{
    public bool Ok => Failure is null;

    public string Describe() => Ok
        ? $"{Subject} — 영역 {Areas} · 성취기준 {Standards} · 평가기준 {Criteria}"
        : $"{Subject} — 멈춤: {Failure}";
}

/// <summary>
/// 평가계획을 나이스에 넣는 흐름을 한 곳에 묶는다 (<see cref="TimetableSession"/> 과 같은 자리).
///
/// 교과 하나마다 <b>세 걸음</b>을 순서대로 밟는다 (기술설계 E-001):
/// <code>영역명 등록 → 성취기준 → 평가기준</code>
/// 걸음마다 저장하고, 넣은 뒤에는 <b>다시 읽어 대조한다</b>(E-006).
/// </summary>
public sealed class EvalPlanSession(INeisEngine engine, NeisSessionController session)
{
    /// <summary>지금 화면에서 읽은 조회 조건 (학년도·학기·학년·교과).</summary>
    public EvalScope? Scope { get; private set; }

    /// <summary>
    /// 나이스가 평가계획(안)관리 화면에 있고 조회까지 됐는지 확인한다.
    /// </summary>
    /// <returns>준비됐으면 null, 아니면 사용자에게 보여 줄 이유</returns>
    public async Task<string?> PreflightAsync(CancellationToken ct = default)
    {
        // 평가계획(안)관리 화면으로 <b>스스로 찾아간다</b>(2026-08-22).
        // 한때 여기서 시간표 목적지를 불러 사용자가 열어 둔 화면을 끌고 가 버렸다 — 목적지를 제대로 넣었다.
        var gate = await session.EnsureReadyAsync(NeisTarget.EvalPlan, null, ct);
        if (gate is not null) return gate;

        var tools = Tools();
        if (tools is null) return "브라우저에 연결되어 있지 않습니다.";

        var reader = tools.Reader;
        if (!await reader.IsEvalScreenAsync())
            return "나이스에서 [학급담임 → 평가계획 → 평가계획(안)관리] 를 열어 주세요.";

        Scope = await reader.ReadScopeAsync();

        return Scope is null ? "조회 조건을 읽지 못했습니다." : null;
    }

    /// <summary>
    /// <b>걸음 ①</b> — 성취기준관리 화면에서 교과 하나의 영역명·성취기준을 넣는다.
    ///
    /// 단계 전환은 <see cref="EnterAsync"/> 로 <b>한 번만</b> 하고, 여기서는 교과만 바꾼다.
    /// </summary>
    public async Task<EvalSubjectResult> RunStandardsAsync(
        EvalSubjectPlan plan,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var tools = Tools();
        if (tools is null) return new(plan.Subject, 0, 0, 0, "브라우저에 연결되어 있지 않습니다.");

        try
        {
            progress?.Report(new($"{plan.Subject} — 나이스 교과를 바꾸고 있어요…"));
            if (await FocusSubjectAsync(tools.Reader, plan.Subject, ct) is { } blocked)
                return new(plan.Subject, 0, 0, 0, blocked);

            // 화면에 이미 올라간 것은 빼고 올린다 — 나이스 일괄업로드는 덮어쓰지 않고 <b>더한다</b>(E-004).
            var already = await ReadStandardsAsync(tools.Reader);
            var pending = EvalUploadRows.Pending(EvalUploadRows.Of(plan), already);

            if (pending.Count == 0)
                return new(plan.Subject, 0, 0, 0);

            progress?.Report(new($"{plan.Subject} — 성취기준 {pending.Count}건을 올리고 있어요…"));

            // 샘플엑셀은 받지 않는다. 양식을 이미 알고 있으니 그대로 만들어 올리고 <b>바로 지운다</b>.
            var file = Path.Combine(Path.GetTempPath(), $"eval_{Guid.NewGuid():N}.xlsx");
            int added;
            try
            {
                EvalUploadWorkbook.Write(file, pending);
                added = await tools.Writer.UploadStandardsAsync(file, ct);
            }
            finally
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception ex) { Services.Diag.Swallow(ex, "일괄업로드 임시파일 정리"); }
            }

            return new(plan.Subject, 0, added, 0);
        }
        catch (OperationCanceledException) { return new(plan.Subject, 0, 0, 0, "중지했습니다."); }
        catch (Exception ex) { return new(plan.Subject, 0, 0, 0, ex.Message); }
    }

    /// <summary>
    /// <b>걸음 ②</b> — 성취기준(평가기준)관리 화면에서 교과 하나의 단계별 평가기준을 넣는다.
    /// </summary>
    public async Task<EvalSubjectResult> RunCriteriaAsync(
        EvalSubjectPlan plan,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var tools = Tools();
        if (tools is null) return new(plan.Subject, 0, 0, 0, "브라우저에 연결되어 있지 않습니다.");

        try
        {
            progress?.Report(new($"{plan.Subject} — 나이스 교과를 바꾸고 있어요…"));
            if (await FocusSubjectAsync(tools.Reader, plan.Subject, ct) is { } blocked)
                return new(plan.Subject, 0, 0, 0, blocked);

            var all = plan.Areas.SelectMany(a => a.Standards).ToList();
            var done = 0;

            foreach (var standard in all)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(
                    $"{plan.Subject} — 평가기준 {done + 1}/{all.Count}", done + 1, all.Count));

                var row = await FindRowAsync(tools.Reader, standard.Standard);
                if (row < 0)
                    return new(plan.Subject, 0, 0, done,
                        $"넣은 성취기준을 화면에서 찾지 못했습니다 — {Short(standard.Standard)}");

                await tools.Writer.WriteCriteriaAsync(row, standard.Criteria, ct);
                done++;
            }

            return new(plan.Subject, 0, 0, done);
        }
        catch (OperationCanceledException) { return new(plan.Subject, 0, 0, 0, "중지했습니다."); }
        catch (Exception ex) { return new(plan.Subject, 0, 0, 0, ex.Message); }
    }

    /// <summary>그 단계 화면으로 옮긴다. 교과를 돌기 전에 <b>한 번만</b> 부른다.</summary>
    public async Task<string?> EnterAsync(EvalStep step, CancellationToken ct = default)
    {
        var tools = Tools();
        if (tools is null) return "브라우저에 연결되어 있지 않습니다.";

        try
        {
            await GoAsync(tools.Reader, step, ct);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// 나이스 교과를 목표로 바꾸고 조회한다. 막히면 사용자에게 보여 줄 이유, 되면 null.
    ///
    /// <b>단계를 바꾸면 교과가 첫 항목으로 되돌아간다</b>(실측 2026-08-22: 평가기준 화면으로 옮기자
    /// 교과가 국어로 되돌아가, 도덕 성취기준을 국어 화면에서 찾다 멈췄다).
    /// 그래서 단계마다·교과마다 여기서 다시 고르고 <b>화면 표시값까지 대조한다</b>.
    /// </summary>
    private async Task<string?> FocusSubjectAsync(
        EvalScreenReader reader, string subject, CancellationToken ct)
    {
        var selected = await EvalPlanSubjectSelector.SelectAndVerifyAsync(engine, subject, ct);
        if (!selected.Ok) return selected.Message;

        if (!await reader.QueryAsync(ct)) return "[조회]를 누르지 못했습니다.";

        for (var i = 0; i < 3 && await reader.DialogTextAsync() is not null; i++)
            await reader.ClickDialogAsync("확인");

        Scope = await reader.ReadScopeAsync();
        if (Scope is null || !string.Equals(Scope.Subject.Trim(), subject.Trim(), StringComparison.Ordinal))
            return $"화면 교과는 '{Scope?.Subject ?? "읽지 못함"}'인데 입력 대상은 '{subject}'입니다. 입력하지 않고 멈췄습니다.";

        return null;
    }


    /// <summary>단계로 옮기고 조회한다. 남아 있던 미저장 행은 조회가 버려 준다.</summary>
    private static async Task GoAsync(EvalScreenReader reader, EvalStep step, CancellationToken ct)
    {
        if (!await reader.GoToAsync(step))
            throw new InvalidOperationException("평가계획 입력 단계를 선택하지 못했습니다.");
        if (!await reader.QueryAsync(ct))
            throw new InvalidOperationException("평가계획 화면에서 [조회]를 누르지 못했습니다.");

        // 조회할 때 "변경사항이 반영되지 않았습니다" 가 뜨면 확인을 눌러 버린다
        for (var i = 0; i < 3 && await reader.DialogTextAsync() is not null; i++)
            await reader.ClickDialogAsync("확인");
    }

    /// <summary>지금 화면에 올라와 있는 (영역명, 성취기준) 쌍.</summary>
    private static async Task<IReadOnlyList<(string Area, string Standard)>> ReadStandardsAsync(
        EvalScreenReader reader)
    {
        var grid = reader.Standards;
        var areaColumn = await grid.ColumnAsync("영역명");
        var standardColumn = await grid.ColumnAsync("성취기준");
        if (areaColumn < 0 || standardColumn < 0) return Array.Empty<(string, string)>();

        var rows = new List<(string, string)>();
        for (var r = 0; r < await grid.RowCountAsync(); r++)
            rows.Add((await grid.CellAsync(r, areaColumn), await grid.CellAsync(r, standardColumn)));

        return rows;
    }

    /// <summary>그 성취기준이 화면 몇 번째 줄인지. 없으면 -1.</summary>
    private static async Task<int> FindRowAsync(EvalScreenReader reader, string standard)
    {
        var grid = reader.Standards;
        var column = await grid.ColumnAsync("성취기준");
        if (column < 0) return -1;

        var want = Squash(standard);

        for (var row = 0; row < await grid.RowCountAsync(); row++)
            if (Squash(await grid.CellAsync(row, column)) == want) return row;

        return -1;
    }

    private EvalPlanTools? Tools() => (engine as NeisEngine)?.EvalPlan;

    private static string Squash(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");

    private static string Short(string s) => s.Length <= 30 ? s : s[..30] + "…";
}
