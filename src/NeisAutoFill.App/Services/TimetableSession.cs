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
public sealed class TimetableSession(INeisEngine engine, NeisSessionController session, TimetableProfileStore profiles)
{
    /// <summary>마지막으로 읽은 카탈로그 — 매핑·계획에서 재사용한다.</summary>
    public TimetableCatalog? Catalog { get; private set; }

    /// <summary>마지막으로 읽은 범위(해시 적용본).</summary>
    public TimetableProfileScope? Scope { get; private set; }

    /// <summary>마지막으로 읽은 주의 셀 값.</summary>
    public TimetableGridSnapshot? Snapshot { get; private set; }

    /// <summary>메뉴가 열리지 않은 셀들 — 학기 시작 전·휴업일. 실패가 아니라 정상 상태다.</summary>
    public HashSet<TimetableCell> Unavailable { get; } = new();

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

        var week = await reader.SelectWeekForDateAsync(targetDate);
        if (week is null)
            return new TimetablePreflight(false, $"{targetDate:yyyy-MM-dd} 이 들어 있는 주차를 찾지 못했습니다.");

        Snapshot = await reader.ReadCurrentWeekAsync();

        // 범위 — 학교·사용자는 여기서 즉시 해시로 바뀌고 원문은 보관하지 않는다(D-012)
        var info = await tools.Scope.ReadAsync();
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
    /// 매핑 화면을 열어 규칙을 확정하고 프로필에 저장한다.
    /// <see cref="PreflightAsync"/> 를 먼저 통과해야 한다. 취소하면 null.
    /// </summary>
    public IReadOnlyList<TimetableMappingRule>? OpenMapping(
        IReadOnlyList<TimetableSourceLesson> lessons, System.Windows.Window? owner)
    {
        if (Catalog is null) return null;

        var saved = Scope is null ? null : profiles.Find(Scope);
        var changed = saved is not null && saved.NeedsRecheck(Catalog.Fingerprint);

        // 목록이 바뀌었으면 살아남은 규칙만 초기값으로 — 확정 표시는 지워져 다시 확인받는다
        var seed = saved is null ? null
            : changed ? saved.RulesStillValid(Catalog) : saved.Rules;

        var vm = new TimetableMappingViewModel(lessons, Catalog, seed, changed);
        var rules = TimetableMappingWindow.Ask(vm, owner);
        if (rules is null) return null;

        if (Scope is not null)
            profiles.Save(new TimetableMappingProfile(Scope, Catalog.Fingerprint, rules, DateTimeOffset.Now));

        return rules;
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

    /// <summary>메뉴가 열리는 평일 셀을 찾아 카탈로그를 읽는다. 열리지 않는 날은 사용 불가로 기록한다.</summary>
    private async Task<TimetableCatalog?> ReadCatalogAsync(TimetableReader reader, CancellationToken ct)
    {
        var dates = Snapshot?.Dates ?? Array.Empty<DateOnly>();

        for (var col = 1; col <= 5; col++)   // 월~금
        {
            ct.ThrowIfCancellationRequested();
            var catalog = await reader.ReadCatalogAsync(0, col);
            if (catalog is not null) return catalog;

            // 이 날은 메뉴가 안 열린다 → 그 날 전체를 사용 불가로 (학기 시작 전 등)
            if (dates.ElementAtOrDefault(col - 1) is { } d && d != default)
                for (var period = 1; period <= 8; period++)
                    Unavailable.Add(new TimetableCell(d, period));
        }
        return null;
    }
}
