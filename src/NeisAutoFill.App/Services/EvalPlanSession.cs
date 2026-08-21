using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Evaluation;

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
        var gate = await session.EnsureReadyAsync(NeisTarget.ClassTimetable, null, ct);
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
    /// 교과 하나를 통째로 넣는다.
    ///
    /// <b>시작할 때 조회를 한 번 누른다</b> — 앞선 시도가 남긴 빈 행이 있으면 저장이 막히는데,
    /// 조회가 그것을 버려 준다(실측 2026-08-21).
    /// </summary>
    public async Task<EvalSubjectResult> RunSubjectAsync(
        EvalSubjectPlan plan,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var tools = Tools();
        if (tools is null) return new(plan.Subject, 0, 0, 0, "브라우저에 연결되어 있지 않습니다.");

        var reader = tools.Reader;
        var writer = tools.Writer;

        try
        {
            // ① 영역명 — 성취기준에서 고를 수 있으려면 먼저 등록돼 있어야 한다
            progress?.Report(new($"{plan.Subject} — 영역명을 등록하고 있어요…"));
            await GoAsync(reader, EvalStep.Standards, ct);

            var areas = await writer.RegisterAreasAsync(plan.Areas.Select(a => a.Name), ct);
            ct.ThrowIfCancellationRequested();

            // ② 성취기준
            progress?.Report(new($"{plan.Subject} — 성취기준을 넣고 있어요…"));

            var rows = plan.Areas
                .SelectMany(a => a.Standards.Select(s => (a.Name, s.Standard, s.Element)))
                .ToList();

            var added = await writer.AddStandardsAsync(rows, ct);
            ct.ThrowIfCancellationRequested();

            // ③ 평가기준 — 성취기준을 하나씩 골라 가며
            await GoAsync(reader, EvalStep.Criteria, ct);

            var all = plan.Areas.SelectMany(a => a.Standards).ToList();
            var done = 0;

            foreach (var standard in all)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(
                    $"{plan.Subject} — 평가기준 {done + 1}/{all.Count}", done + 1, all.Count));

                var row = await FindRowAsync(reader, standard.Standard);
                if (row < 0)
                    return new(plan.Subject, areas.Count, added, done,
                        $"넣은 성취기준을 화면에서 찾지 못했습니다 — {Short(standard.Standard)}");

                await writer.WriteCriteriaAsync(row, standard.Criteria, ct);
                done++;
            }

            return new(plan.Subject, areas.Count, added, done);
        }
        catch (OperationCanceledException)
        {
            return new(plan.Subject, 0, 0, 0, "중지했습니다.");
        }
        catch (Exception ex)
        {
            return new(plan.Subject, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>단계로 옮기고 조회한다. 남아 있던 미저장 행은 조회가 버려 준다.</summary>
    private static async Task GoAsync(EvalScreenReader reader, EvalStep step, CancellationToken ct)
    {
        await reader.GoToAsync(step);
        await reader.QueryAsync(ct);

        // 조회할 때 "변경사항이 반영되지 않았습니다" 가 뜨면 확인을 눌러 버린다
        for (var i = 0; i < 3 && await reader.DialogTextAsync() is not null; i++)
            await reader.ClickDialogAsync("확인");
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
