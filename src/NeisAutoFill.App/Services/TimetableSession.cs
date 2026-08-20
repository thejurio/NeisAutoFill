using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.Services;

/// <summary>시간표 연결 상태 점검 결과 — 사용자에게 그대로 보여 준다(개인정보 없음).</summary>
/// <param name="Ok">입력을 시작할 수 있는 상태인지</param>
/// <param name="Message">사용자에게 보여 줄 요약</param>
public sealed record TimetablePreflight(bool Ok, string Message);

/// <summary>
/// 시간표 작업 한 번의 흐름을 묶는다 — 화면 이동 → 조회 → 주차 선택 → 카탈로그 → 범위 → 매핑 (기술설계 §10·§13).
///
/// 나이스를 <b>읽기만</b> 한다. 셀 입력·저장은 이 클래스가 하지 않는다(T7 에서 별도로 승급).
/// </summary>
public sealed class TimetableSession(
    INeisEngine engine,
    NeisSessionController session,
    TimetableProfileStore profiles,
    TimetableCheckpointStore checkpoints)
{
    /// <summary>마지막으로 읽은 카탈로그 — 매핑·계획에서 재사용한다.</summary>
    public TimetableCatalog? Catalog { get; private set; }

    /// <summary>마지막으로 읽은 범위(해시 적용본).</summary>
    public TimetableProfileScope? Scope { get; private set; }

    /// <summary>마지막으로 읽은 주의 셀 값.</summary>
    public TimetableGridSnapshot? Snapshot { get; private set; }

    /// <summary>메뉴가 열리지 않은 셀들 — 학기 시작 전·휴업일. 실패가 아니라 정상 상태다.</summary>
    public HashSet<TimetableCell> Unavailable { get; } = new();

    /// <summary>
    /// 지금 로그인한 교사 이름 = <b>이 학급의 담임</b>.
    /// 학급시간표관리는 담임 화면이므로 로그인 사용자가 곧 담임이다.
    /// <b>메모리에만 둔다</b> — 파일에 남는 범위(Scope)에는 해시로만 들어간다(D-012).
    /// </summary>
    public string? HomeroomTeacher { get; private set; }

    /// <summary>나이스가 아는 학기 범위 — 주차 목록의 처음과 끝.</summary>
    public DateOnly? TermStart { get; private set; }
    public DateOnly? TermEnd { get; private set; }

    /// <summary>
    /// 화면을 준비하고 읽을 수 있는지 확인한다. 입력은 하지 않는다.
    /// 사용자가 풀어야 하는 상황(로그인 등)이면 그 문구를 그대로 돌려준다.
    /// </summary>
    public async Task<TimetablePreflight> PreflightAsync(
        DateOnly targetDate, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var gate = await session.EnsureReadyAsync(NeisTarget.ClassTimetable, progress, ct);
        if (gate is not null) return new TimetablePreflight(false, gate);

        var tools = engine.Timetable;
        if (tools is null) return new TimetablePreflight(false, "브라우저에 연결되어 있지 않습니다.");

        var reader = tools.Reader;
        if (!await reader.IsTimetableScreenAsync())
            return new TimetablePreflight(false, "학급시간표관리 화면이 아닙니다.");

        // 조회 → 주차 목록만 채워진다. 그리드는 주차를 골라야 생긴다(실측 §4-A)
        progress?.Report(new("시간표를 조회하고 있어요…"));
        if (!await reader.HasGridAsync())
        {
            var (ok, why) = await engine.QueryAsync(ct);
            if (!ok) return new TimetablePreflight(false, $"조회하지 못했습니다. {why}");
        }

        var weeks = await reader.ReadWeeksAsync();
        if (weeks.Count > 0)
        {
            TermStart = weeks.Min(w => w.Start);
            TermEnd = weeks.Max(w => w.End);
        }

        // 문서 날짜가 이 학기에 없어도 여기서 멈추지 않는다 — 과목·교사 목록은 아무 주에서나 읽으면 되고,
        // 기간이 안 맞는다는 사실은 다음 단계(기간 선택)에서 두 범위를 나란히 보여 주며 알린다.
        var week = await reader.SelectWeekForDateAsync(targetDate)
                   ?? await SelectReadableWeekAsync(reader, weeks);

        if (week is null)
            return new TimetablePreflight(false, "시간표를 읽을 주차를 찾지 못했습니다. 나이스에서 조회가 됐는지 확인하세요.");

        Snapshot = await reader.ReadCurrentWeekAsync();

        // 범위 — 학교·사용자는 여기서 즉시 해시로 바뀌고 원문은 보관하지 않는다(D-012)
        var info = await tools.Scope.ReadAsync();

        // "진두성(thejurio)" 처럼 이름 뒤에 계정이 붙어 온다 — 이름만 떼어 둔다
        HomeroomTeacher = info?.User is { Length: > 0 } user
            ? (user.IndexOf('(') is var i && i > 0 ? user[..i] : user).Trim()
            : null;

        Scope = info is null ? null : TimetableProfileScope.Create(
            info.Host, info.School, info.User, info.SchoolYear, info.Semester, info.Grade, info.ClassName);

        // 카탈로그 — 메뉴가 열리는 첫 평일 셀에서 읽는다
        progress?.Report(new("과목·교사 목록을 읽고 있어요…"));
        Unavailable.Clear();
        Catalog = await ReadCatalogAsync(reader, ct);

        if (Catalog is null)
            return new TimetablePreflight(false,
                $"{week.Name} 에서는 과목·교사 목록을 읽을 수 없었습니다. " +
                "수업이 있는 다른 주를 골라 주세요 (학기 시작 전 주에서는 메뉴가 열리지 않습니다).");

        var saved = Scope is null ? null : profiles.Find(Scope);
        var savedText = saved is null ? "저장된 매핑 없음"
            : saved.NeedsRecheck(Catalog.Fingerprint) ? "저장된 매핑 있음 — 목록이 바뀌어 재확인 필요"
            : $"저장된 매핑 {saved.Rules.Count}건 재사용 가능";

        return new TimetablePreflight(true,
            $"{Scope?.Describe() ?? "범위 미확인"} · {week.Name}\n" +
            $"셀 {Snapshot.Cells.Count}칸(값 있음 {Snapshot.Cells.Count(c => c.Value.Length > 0)}칸) · " +
            $"입력 가능 항목 {Catalog.Assignable.Count}개 · 제외 명령 {Catalog.Commands.Count}개\n" +
            $"{savedText}");
    }

    /// <summary>
    /// 이 학급·학기에 저장해 둔 매핑 규칙 (기술설계 §13).
    /// 나이스 목록이 바뀌었으면 <b>살아남은 규칙만</b> 돌려준다 — 사라진 교사를 가리키는 규칙은 버린다.
    /// </summary>
    /// <param name="catalogChanged">목록이 바뀌어 일부가 버려졌는지 — 사용자에게 알려야 한다</param>
    public IReadOnlyList<TimetableMappingRule> LoadRules(out bool catalogChanged)
    {
        catalogChanged = false;
        if (Scope is null || Catalog is null) return Array.Empty<TimetableMappingRule>();

        var saved = profiles.Find(Scope);
        if (saved is null) return Array.Empty<TimetableMappingRule>();

        catalogChanged = saved.NeedsRecheck(Catalog.Fingerprint);
        return catalogChanged ? saved.RulesStillValid(Catalog) : saved.Rules;
    }

    /// <summary>규칙을 이 학급·학기 앞으로 저장한다. 다음에 열면 그대로 되살아난다.</summary>
    public void SaveRules(IReadOnlyList<TimetableMappingRule> rules)
    {
        if (Scope is null || Catalog is null) return;
        profiles.Save(new TimetableMappingProfile(Scope, Catalog.Fingerprint, rules, DateTimeOffset.Now));
    }

    /// <summary>
    /// 이 계획을 이어서 할 기록을 가져온다 (로드맵 T8).
    /// 원본이나 나이스 목록이 바뀌었으면 이어서 쓰지 않고 처음부터 시작한다.
    /// </summary>
    /// <param name="blocker">이어서 못 쓰는 이유 — 사용자에게 그대로 보여 준다. 이어서 쓸 수 있으면 null</param>
    public TimetableRunCheckpoint ResumePoint(TimetablePlan plan, out string? blocker)
    {
        // 범위를 못 읽었으면 기록을 남길 자리가 없다 — 이번 실행에서만 쓰는 임시 기록으로 진행한다
        var scope = Scope ?? TimetableProfileScope.Create("", "", "", 0, 0, 0, "");
        var catalogFingerprint = Catalog?.Fingerprint ?? "";

        return checkpoints.Resume(scope, plan.Fingerprint, catalogFingerprint, out blocker);
    }

    /// <summary>
    /// 계획을 실제로 입력·저장한다 (로드맵 T8). <b>동의를 이미 받은 뒤에만</b> 부른다.
    ///
    /// 한 주가 끝날 때마다 기록을 즉시 파일에 남긴다 — 도중에 앱이 꺼져도 그 주까지는 지켜진다.
    /// </summary>
    public async Task<BatchRunResult> RunBatchAsync(
        TimetablePlan plan,
        IReadOnlyList<TimetableSourceLesson> lessons,
        IReadOnlyList<TimetableMappingRule> rules,
        TimetableRunCheckpoint checkpoint,
        bool allowOverwrite = false,
        IProgress<ProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        var tools = engine.Timetable
            ?? throw new InvalidOperationException("브라우저에 연결되어 있지 않습니다.");

        var request = new TimetableRunRequest(
            lessons, rules, Catalog!,
            plan.ByWeek.Select(w => w.Key).ToList(),
            TermStart, TermEnd, allowOverwrite);

        var canPersist = Scope is not null;

        return await tools.Batch.RunAsync(
            request, checkpoint,
            onCheckpoint: c => { if (canPersist) checkpoints.Save(c); },
            progress, ct);
    }

    /// <summary>저장해 둔 교사 배정을 지운다 — 자동 배정부터 다시 하고 싶을 때.</summary>
    public void ClearRules()
    {
        if (Scope is not null) profiles.Delete(Scope);
    }

    /// <summary>이 범위의 재개 기록을 지운다 — 처음부터 다시 하고 싶을 때.</summary>
    public void ClearCheckpoint()
    {
        if (Scope is not null) checkpoints.Delete(Scope);
    }

    /// <summary>지금 읽은 화면 상태 — 실행 계획을 만들 때 넘긴다.</summary>
    public TimetableScreenState ScreenState()
    {
        if (Snapshot is null) return TimetableScreenState.Empty;

        var current = Snapshot.Cells
            .Where(c => c.Value.Length > 0)
            .ToDictionary(c => c.Key, c => TimetableMenuParser.Parse(c.Value).StableKey);

        return new TimetableScreenState(current, Unavailable, TermStart, TermEnd);
    }

    /// <summary>
    /// 문서 날짜가 이 학기에 없을 때 대신 띄울 주.
    /// 오늘이 든 주를 먼저 보고, 없으면 두 번째 주를 쓴다 —
    /// 첫 주는 개학식 등으로 수업이 없어 메뉴가 안 열리는 경우가 있다(실측).
    /// </summary>
    private static async Task<TimetableWeek?> SelectReadableWeekAsync(
        TimetableReader reader, IReadOnlyList<TimetableWeek> weeks)
    {
        if (weeks.Count == 0) return null;

        var today = DateOnly.FromDateTime(DateTime.Today);
        var pick = weeks.FirstOrDefault(w => today >= w.Start && today <= w.End)
                   ?? weeks.ElementAtOrDefault(1)
                   ?? weeks[0];

        await reader.SelectWeekAsync(pick.Index);
        return pick;
    }

    /// <summary>메뉴가 열리는 평일 셀을 찾아 카탈로그를 읽는다. 열리지 않는 날은 사용 불가로 기록한다.</summary>
    private async Task<TimetableCatalog?> ReadCatalogAsync(TimetableReader reader, CancellationToken ct)
    {
        var dates = Snapshot?.Dates ?? Array.Empty<DateOnly>();

        // <b>이미 값이 있는 칸부터 찔러 본다.</b> 값이 있으면 메뉴가 반드시 열린다.
        // 앞에서부터 훑으면 학기 첫 주의 월·화(개학 전)에서 각각 헛기다림이 생긴다 —
        // 그것만으로 카탈로그 읽기가 6초에서 안 줄었다(2026-08-20).
        foreach (var (row, col) in ProbeOrder())
        {
            ct.ThrowIfCancellationRequested();

            var catalog = await reader.ReadCatalogAsync(row, col);
            if (catalog is not null) return catalog;

            // 이 날은 메뉴가 안 열린다 → 그 날 전체를 사용 불가로 (학기 시작 전 등)
            if (dates.ElementAtOrDefault(col - 1) is { } d && d != default)
                for (var period = 1; period <= 8; period++)
                    Unavailable.Add(new TimetableCell(d, period));
        }
        return null;
    }

    /// <summary>
    /// 카탈로그를 읽어 볼 (행, 열) 순서.
    ///
    /// <b>학기 밖 날짜는 아예 건너뛴다.</b> 학기 첫 주는 개학 전 요일이 섞여 있는데,
    /// 그런 날은 메뉴가 열리지 않아 찔러 볼 때마다 헛기다림이 생긴다(요일당 0.8초).
    /// 날짜로 거르면 값이 하나도 없는 빈 시간표에서도 통한다 — 처음 넣을 때가 바로 그렇다.
    ///
    /// 그다음엔 <b>값이 있는 칸</b>을 먼저 본다. 값이 있으면 메뉴가 반드시 열린다.
    /// 한 요일당 한 번만 찔러 본다.
    /// </summary>
    private IReadOnlyList<(int Row, int Col)> ProbeOrder()
    {
        var order = new List<(int Row, int Col)>();
        var seen = new HashSet<int>();

        if (Snapshot is not { } snap) return new[] { (0, 1), (0, 2), (0, 3), (0, 4), (0, 5) };

        var periods = snap.Cells.Keys.Select(c => c.Period).Distinct().OrderBy(p => p).ToList();

        bool InTerm(int col) =>
            snap.Columns.TryGetValue(col, out var d) &&
            (TermStart is null || d >= TermStart) &&
            (TermEnd is null || d <= TermEnd);

        // ① 값이 있는 칸 (메뉴가 열리는 것이 확실하다)
        foreach (var cell in snap.Cells.Where(c => c.Value.Length > 0)
                     .Select(c => c.Key).OrderBy(c => c.Period).ThenBy(c => c.Date))
        {
            var col = snap.ColumnOf(cell.Date);
            if (col is < 1 or > 5 || !InTerm(col) || !seen.Add(col)) continue;

            var row = periods.IndexOf(cell.Period);
            if (row >= 0) order.Add((row, col));
        }

        // ② 학기 안이지만 값이 없는 요일
        for (var col = 1; col <= 5; col++)
            if (InTerm(col) && seen.Add(col)) order.Add((0, col));

        // ③ 학기 밖 요일 — 여기까지 왔다면 앞이 다 실패한 것이다. 마지막으로만 본다.
        for (var col = 1; col <= 5; col++)
            if (seen.Add(col)) order.Add((0, col));

        return order;
    }
}
