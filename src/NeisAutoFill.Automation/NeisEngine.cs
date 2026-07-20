using Microsoft.Playwright;
using NeisAutoFill.Automation.Abstractions;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Automation;

/// <summary>
/// 나이스 교과별 평가 자동입력 엔진 (Playwright). 개발노트 §4 알고리즘 이식.
/// 안전정책: 과목 검증, dry-run, 척도 화이트리스트, 입력 후 재검증+재시도, 저장 미자동화, 멱등성.
/// </summary>
public sealed class NeisEngine(EngineOptions options) : INeisEngine, IAsyncDisposable
{
    private IPlaywright? _pw;
    private IBrowser? _browser;
    private IPage? _page;
    private GridScroller? _scroller;
    private ComboBoxDriver? _combo;

    public bool Connected => _page is not null;

    public void LaunchEdge() => new EdgeLauncher(options).Launch();

    public async Task<bool> AttachAsync(CancellationToken ct = default)
    {
        _pw ??= await Playwright.CreateAsync();
        _browser = await _pw.Chromium.ConnectOverCDPAsync($"http://{options.DebugAddress}");

        foreach (var context in _browser.Contexts)
        {
            foreach (var page in context.Pages)
            {
                if (page.Url.Contains("neis.go.kr"))
                {
                    _page = page;
                    _scroller = new GridScroller(page);
                    _combo = new ComboBoxDriver(page);
                    return true;
                }
            }
        }
        throw new InvalidOperationException("neis.go.kr 탭을 찾지 못했습니다. NEIS에 접속해 주세요.");
    }

    /// <summary>연결이 살아있는지 확인. 죽었으면 상태를 비우고 false. (자동 재연결 루프용)</summary>
    public async Task<bool> IsAliveAsync()
    {
        if (_page is null) return false;
        try
        {
            _ = await _page.TitleAsync();          // 페이지 응답 확인
            if (_page.IsClosed) throw new InvalidOperationException("page closed");
            if (!_page.Url.Contains("neis.go.kr")) return true;   // 다른 탭으로 이동해도 연결 유지
            return true;
        }
        catch
        {
            _page = null; _scroller = null; _combo = null;
            try { if (_browser is not null) await _browser.CloseAsync(); } catch { }
            _browser = null;
            return false;
        }
    }

    /// <summary>지금 나이스 상황 판별 (F9 M8) — 연결·로그인·화면 종류.
    /// 실측 확정(2026-07-19): 로그인=문서제목 "로그인", 화면종류=app-tit 텍스트.</summary>
    public async Task<NeisStatus> DetectStatusAsync(CancellationToken ct = default)
    {
        if (_page is null) return new NeisStatus(NeisScreenKind.Disconnected);

        string url, docTitle;
        try
        {
            if (_page.IsClosed) { _page = null; _scroller = null; _combo = null; return new NeisStatus(NeisScreenKind.Disconnected); }
            url = _page.Url;
            docTitle = await _page.TitleAsync();   // 페이지가 응답하는지 확인 겸 제목 확보
        }
        catch
        {
            _page = null; _scroller = null; _combo = null;
            return new NeisStatus(NeisScreenKind.Disconnected);
        }

        if (!url.Contains("neis.go.kr"))
            return new NeisStatus(NeisScreenKind.NotNeisTab, url);

        // 로그인 안 된 상태 — 실측 두 화면 (메인은 "4세대 나이스 시스템"):
        //   · 로그인 화면: 제목 "로그인" (인증서 방식이라 비밀번호칸 없음)
        //   · 로그아웃 직후: 제목 "로그아웃", URL executeNeisLogout.do
        if (docTitle.Contains("로그인") || docTitle.Contains("로그아웃") || url.Contains("Logout"))
            return new NeisStatus(NeisScreenKind.LoggedOut, url);

        // 화면 제목(app-tit)으로 판별 — 실측: 교과평가/학기말종합의견/교과학습발달상황 이 그대로 들어온다.
        // 교과별 평가(성적 등급) 화면이면 입력 준비. 그리드는 조회를 눌러야 뜨므로 판별에 요구하지 않는다
        // (조회 전에도 '교과평가' 화면이면 준비 상태로 본다 → 호출부가 조회를 누른다).
        try
        {
            var title = await ReadScreenTitleAsync();
            if (title.Contains("교과평가"))
            {
                var (_, subject) = await FindSubjectComboAsync();   // 화면 과목은 있으면 표시
                return new NeisStatus(NeisScreenKind.EvaluationReady, url, subject);
            }
        }
        catch { /* 판별 중 오류는 아래 폴백으로 */ }

        return new NeisStatus(NeisScreenKind.OtherNeisPage, url);
    }

    /// <summary>목표 화면으로 앱이 직접 이동 (F9 M10). 세 화면 모두 메뉴 경로:
    /// 학급담임(상단 네비 — 어느 페이지에서도 보임) → 학생평가 → (교과평가|학기말종합의견|교과학습발달상황).
    /// 상단 '학급담임'을 먼저 눌러 그 카테고리로 돌아오므로 다른 메뉴 화면에서도 출발할 수 있다.
    /// 실제 그 화면(그리드 등장)이 확인될 때만 true. 막힌 단계만 progress 로 알린다.</summary>
    public async Task<bool> NavigateToAsync(NeisTarget target, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (_page is null) return false;
        await DismissAlertsAsync(ct);   // 남은 알림이 메뉴 클릭을 막지 않게
        // 마지막 단계 라벨(접미사 포함 우선)·이동 후 탭·화면 제목(app-tit 실측값)
        var (thirdName, thirdLabels, tabLabels, screenName, title) = target switch
        {
            NeisTarget.Evaluation =>
                ("교과평가", new[] { "교과평가 3단계", "교과평가" }, new[] { "교과별 평가", "교과별평가" }, "교과별 평가", "교과평가"),
            NeisTarget.TermOpinion =>
                ("학기말종합의견", new[] { "학기말종합의견 3단계", "학기말종합의견" }, System.Array.Empty<string>(), "학기말종합의견", "학기말종합의견"),
            NeisTarget.SubjectDevelopment =>
                ("교과학습발달상황", new[] { "교과학습발달상황 3단계", "교과학습발달상황" }, System.Array.Empty<string>(), "교과학습발달상황", "교과학습발달상황"),
            _ => ("교과평가", new[] { "교과평가" }, System.Array.Empty<string>(), "교과평가", "교과평가"),
        };

        // 이미 그 화면이면 생략 — 제목(app-tit)으로 판별. 서술문 화면은 조회 전이라 그리드가 없으니
        // 제목만으로 판단한다 (그리드 요구하면 조회 전엔 영영 도착으로 안 잡힘).
        if ((await ReadScreenTitleAsync()).Contains(title)) return true;

        var steps = new List<(string name, string[] labels)>
        {
            ("학급담임", new[] { "학급담임 0단계", "학급담임" }),
            ("학생평가", new[] { "학생평가 2단계", "학생평가" }),
            (thirdName, thirdLabels),
        };
        if (tabLabels.Length > 0) steps.Add((screenName, tabLabels));   // 교과평가만 하위 탭

        progress?.Report(new($"{screenName} 화면으로 이동하고 있어요…"));
        try
        {
            foreach (var (name, labels) in steps)
            {
                if (await ClickMenuLabelAsync(labels, ct) is null)   // 성공 단계는 조용히, 막힌 단계만 알림
                {
                    progress?.Report(new($"'{name}' 메뉴를 찾지 못해 이동을 멈췄어요"));
                    return false;
                }
                await Task.Delay(800, ct);           // 하위 메뉴/화면 렌더 대기
            }

            // 도착 확인 — 화면 제목(app-tit)만으로 판별. 서술문 화면은 조회를 눌러야 그리드가 뜨므로
            // 도착 판정에 그리드를 요구하면 안 된다 (조회는 호출부가 이어서 누른다).
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if ((await ReadScreenTitleAsync()).Contains(title)) { await Task.Delay(500, ct); return true; }
                await Task.Delay(600, ct);
            }
            progress?.Report(new($"{screenName} 화면 확인이 안 돼요 (제목이 바뀌지 않음)"));
        }
        catch { /* 실패는 false — 호출부가 안내 */ }
        return false;
    }

    /// <summary>메뉴/탭 항목을 라벨로 클릭. 텍스트엔 "0단계 메뉴항목" 등 접미사가 붙으므로 '포함' 매칭한다.
    /// 실제 상호작용 요소(a·menuitem·tab)를 우선하고, 화면에 보이게 스크롤한 뒤 클릭.
    /// 렌더 지연 대비 몇 초 폴링. 클릭 성공하면 '무엇을(태그)' 눌렀는지 문자열 반환, 실패면 null.</summary>
    private async Task<string?> ClickMenuLabelAsync(string[] labels, CancellationToken ct)
    {
        // 상호작용 요소 우선(a/menuitem/tab/link) → 없으면 wrapper(li) 폴백
        var selectors = new[] { "a, [role='menuitem'], [role='tab'], [role='link']", "li" };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sel in selectors)
            foreach (var label in labels)
            {
                var loc = _page!.Locator(sel).Filter(new LocatorFilterOptions { HasText = label });
                int n = await loc.CountAsync();
                for (int i = 0; i < n; i++)
                {
                    var el = loc.Nth(i);
                    try
                    {
                        if (!await el.IsVisibleAsync()) continue;
                        try { await el.ScrollIntoViewIfNeededAsync(); } catch { }
                        await el.ClickAsync(new LocatorClickOptions { Timeout = 2000 });
                        return $"{label}/{sel.Split(',')[0]}";
                    }
                    catch { /* 다음 후보 */ }
                }
            }
            await Task.Delay(400, ct);   // 하위 메뉴/화면 렌더 대기 후 재시도
        }
        return null;
    }

    /// <summary>현재 화면 제목(app-tit 요소 텍스트). 실측: "교과평가"/"학기말종합의견"/"교과학습발달상황".
    /// 없으면 빈 문자열. 화면 종류 판별·도착 확인의 가장 확실한 신호 (F9 M10).</summary>
    private async Task<string> ReadScreenTitleAsync()
    {
        if (_page is null) return "";
        try
        {
            var tit = _page.Locator("div.app-tit");
            int n = await tit.CountAsync();
            for (int i = 0; i < n; i++)
            {
                if (!await tit.Nth(i).IsVisibleAsync()) continue;
                var t = (await tit.Nth(i).InnerTextAsync() ?? "").Trim();
                if (t.Length > 0) return t;
            }
        }
        catch { /* 없으면 빈 문자열 */ }
        return "";
    }

    public async Task<string?> GetCurrentSubjectAsync(CancellationToken ct = default)
    {
        var (_, subject) = await FindSubjectComboAsync();
        return subject;
    }

    /// <summary>
    /// 화면의 과목 콤보 탐색. 교과별 평가는 aria-label "교과, 국어".
    /// ★ 종합의견 화면은 라벨이 전부 "학기, …" 로 잘못 붙어 있음 (2026-07-17 진단 실측)
    ///   → 조회조건 라벨(학년도/학년/학기/반/교과) 콤보 중 값이 숫자가 아닌 것을 과목으로 본다.
    /// </summary>
    /// <summary>직전 과목 콤보 탐색이 라벨 버그 폴백으로 찾았는지. 정상 '교과' 라벨이 있으면 false.
    /// 나이스가 라벨 버그를 고치면 자연히 false 가 되어, 폴백 의존이 사라졌음을 알 수 있다.</summary>
    public bool LastSubjectComboUsedFallback { get; private set; }

    /// <summary>화면의 aria-label 콤보들과 그 라벨 목록(인덱스 보존)을 함께 반환.
    /// 과목·학년·반 콤보 탐색이 공유한다.
    /// ★ JS 일괄 읽기 1회 — 교과별평가 화면은 그리드 행마다 등급 콤보가 있어(17명이면 30~40개)
    ///   콤보별 RPC(IsVisible+GetAttribute)로 돌면 폴링 한 번에 수 초씩 걸렸다 (속도 병목 실측).</summary>
    private async Task<(ILocator Combos, List<string?> Labels)> ReadComboLabelsAsync()
    {
        var page = RequirePage();
        var combos = page.Locator("div[role='combobox'][aria-label]");
        // querySelectorAll 순서 = Locator Nth 순서 (둘 다 DOM 순서) — 인덱스 정합 보장
        var labels = await page.EvaluateAsync<string?[]>(
            @"() => [...document.querySelectorAll(""div[role='combobox'][aria-label]"")]
                    .map(el => el.offsetParent !== null ? el.getAttribute('aria-label') : null)");
        return (combos, labels?.ToList() ?? new List<string?>());
    }

    private async Task<(ILocator? Combo, string? Subject)> FindSubjectComboAsync()
    {
        var (combos, labels) = await ReadComboLabelsAsync();

        // 분류·선택은 순수 로직(테스트됨). 정상 '교과' 우선, 없으면 라벨 버그 폴백.
        var (idx, value, usedFallback) = Core.SubjectComboClassifier.Pick(labels);
        LastSubjectComboUsedFallback = usedFallback;
        return idx >= 0 ? (combos.Nth(idx), value) : (null, null);
    }

    /// <summary>조회조건 콤보를 라벨 키("학년"/"반")로 찾는다 (전담 반·학년 전환용, F9 M6).
    /// prefer: 같은 라벨이 여럿일 때(예: 학년도 vs 학년) 값으로 진짜 대상을 고른다.</summary>
    private async Task<(ILocator? Combo, string? Value)> FindQueryComboAsync(string key, Func<string, bool>? prefer = null)
    {
        var (combos, labels) = await ReadComboLabelsAsync();
        var (idx, value) = Core.SubjectComboClassifier.FindQueryCombo(labels, key, prefer);
        return idx >= 0 ? (combos.Nth(idx), value) : (null, null);
    }

    private static string DigitsOf(string? s) =>
        new string((s ?? "").Where(char.IsDigit).ToArray());

    // ── Phase 5.5 전과목 자동 업로드 ────────────────────────────
    // ★ 버튼·대화상자 셀렉터는 잠정 (NeisSelectors 주석 참조) — 첫 실기기 실행은 반드시 지켜볼 것.

    public async Task<IReadOnlyList<string>> ReadSubjectOptionsAsync(CancellationToken ct = default)
    {
        RequirePage();
        var (combo, _) = await FindSubjectComboAsync();
        if (combo is null) return Array.Empty<string>();
        return await _combo!.OpenAndReadOptionsAsync(combo);
    }

    public async Task<(bool Ok, string Why)> SelectSubjectAsync(string subjectName, CancellationToken ct = default)
    {
        var page = RequirePage();
        await DismissAlertsAsync(ct);   // 남은 알림이 콤보 조작을 막지 않게

        var (combo, current) = await FindSubjectComboAsync();
        if (current == subjectName) return (true, "이미 조회됨");
        if (combo is null) return (false, "화면에서 과목 콤보를 찾지 못했습니다 (교과별 평가/종합의견 화면인지 확인)");

        var pick = await _combo!.OpenAndPickAsync(combo, subjectName);
        if (!pick.Ok)
            return (false, pick.Reason.StartsWith("옵션")
                ? Abstractions.INeisEngine.SubjectNotInList   // 이름만 다른 경우 — 호출자가 사용자 확인으로 진행 가능
                : $"과목 선택 실패: {pick.Reason}");

        var query = await FindButtonAsync(NeisSelectors.QueryButtonName);
        if (query is null) return (false, "[조회] 버튼을 찾지 못했습니다");
        await query.ClickAsync();

        // 화면 갱신 대기 — 콤보가 대상 과목을 표시하고 그리드가 다시 잡힐 때까지
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct);
            try
            {
                if (await GetCurrentSubjectAsync(ct) == subjectName &&
                    await FindGridAsync(page) is not null)
                {
                    await Task.Delay(400, ct);   // 그리드 데이터 렌더 여유
                    return (true, "");
                }
            }
            catch { /* 갱신 중 일시 오류는 무시하고 재시도 */ }
        }
        return (false, "조회 후 화면 갱신을 확인하지 못했습니다");
    }

    // ── F9 M6 전담: 학년·반 조회조건 전환 ────────────────────────
    // ★ 첫 실기기 실행은 반드시 지켜볼 것 — 옵션 텍스트 형식("5" vs "5학년")은 실측 미확정.
    //   현재값과 같으면 콤보를 열지 않고 건너뛰므로, 자기 반(예: 5-1) 확인은 안전하다.

    public async Task<(bool Ok, string Why)> SelectClassAsync(
        int grade, string @class, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var page = RequirePage();
        await DismissAlertsAsync(ct);   // 남은 알림이 콤보 조작을 막지 않게

        progress?.Report(new($"학년 콤보 확인 중 (목표 {grade}학년)…"));
        var g = await PickQueryComboAsync("학년", grade, progress, ct);
        if (!g.Ok) return g;

        int classNum = int.TryParse(DigitsOf(@class), out var cn) ? cn : 0;
        progress?.Report(new($"반 콤보 확인 중 (목표 {classNum}반)…"));
        var c = await PickQueryComboAsync("반", classNum, progress, ct);
        if (!c.Ok) return c;

        // 둘 다 이미 목표값이면 화면 그대로 — 불필요한 [조회] 생략
        if (g.Why == "skip" && c.Why == "skip")
        {
            progress?.Report(new($"이미 {grade}-{classNum} 화면입니다 (조회 생략)"));
            return (true, "이미 해당 학년·반");
        }

        var query = await FindButtonAsync(NeisSelectors.QueryButtonName);
        if (query is null) return (false, "[조회] 버튼을 찾지 못했습니다");
        progress?.Report(new("[조회] 실행…"));
        await query.ClickAsync();

        // 그리드가 다시 잡힐 때까지 대기
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct);
            try
            {
                if (await FindGridAsync(page) is not null)
                {
                    await Task.Delay(400, ct);   // 렌더 여유
                    return (true, "");
                }
            }
            catch { /* 갱신 중 일시 오류 무시 */ }
        }
        return (false, "조회 후 화면 갱신을 확인하지 못했습니다");
    }

    /// <summary>종합의견·세특 화면의 학년·반·교과를 맞추고 [조회] 한다 (F9 M10).
    /// 이 화면들은 조회조건 라벨이 전부 "학기"로 깨져 있어 값(성질) 기반으로 콤보를 찾는다(ClassifyNarrativeAxis).</summary>
    public async Task<(bool Ok, string Why)> SelectNarrativeAxisAsync(
        int grade, string @class, string subject, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var page = RequirePage();
        await DismissAlertsAsync(ct);   // 남은 알림이 콤보 조작을 막지 않게
        int classNum = int.TryParse(DigitsOf(@class), out var cn) ? cn : 0;

        progress?.Report(new($"학년·반·교과 확인 중 (목표 {grade}-{classNum} {subject})…"));
        var g = await PickAxisComboAsync(Axis.Grade, grade.ToString(), progress, ct);
        if (!g.Ok) return g;
        var c = await PickAxisComboAsync(Axis.Class, classNum.ToString(), progress, ct);
        if (!c.Ok) return c;
        var s = await PickAxisComboAsync(Axis.Subject, subject, progress, ct);
        if (!s.Ok) return s;

        if (g.Why == "skip" && c.Why == "skip" && s.Why == "skip")
        {
            progress?.Report(new($"이미 {grade}-{classNum} {subject} 화면입니다 (조회 생략)"));
            return (true, "이미 해당 조합");
        }

        var query = await FindButtonAsync(NeisSelectors.QueryButtonName);
        if (query is null) return (false, "[조회] 버튼을 찾지 못했습니다");
        await query.ClickAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct);
            try { if (await FindGridAsync(page) is not null) { await Task.Delay(400, ct); return (true, ""); } }
            catch { /* 갱신 중 일시 오류 무시 */ }
        }
        return (false, "조회 후 화면 갱신을 확인하지 못했습니다");
    }

    private enum Axis { Grade, Class, Subject }

    /// <summary>세특/종합의견 화면 축(학년·반·교과) 콤보를 값 기반으로 찾아 목표로 맞춘다.
    /// 이미 목표면 Why="skip". 학년·반은 숫자, 교과는 텍스트 비교.</summary>
    private async Task<(bool Ok, string Why)> PickAxisComboAsync(
        Axis axis, string target, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var name = axis switch { Axis.Grade => "학년", Axis.Class => "반", _ => "교과" };
        var (combos, labels) = await ReadComboLabelsAsync();
        var (gi, ci, si) = Core.SubjectComboClassifier.ClassifyNarrativeAxis(labels);
        int idx = axis switch { Axis.Grade => gi, Axis.Class => ci, _ => si };
        if (idx < 0) return (false, $"{name} 콤보를 화면에서 찾지 못했습니다 (세특/종합의견 화면인지 확인)");

        var combo = combos.Nth(idx);
        var current = await ComboBoxDriver.ReadValueAsync(combo);
        bool match = axis == Axis.Subject
            ? current.Trim() == target.Trim()
            : DigitsOf(current) == DigitsOf(target);
        if (match) return (true, "skip");

        var options = await _combo!.OpenAndReadOptionsAsync(combo);
        var pickText = axis == Axis.Subject
            ? options.FirstOrDefault(o => o.Trim() == target.Trim())
            : options.FirstOrDefault(o => DigitsOf(o) == DigitsOf(target));
        if (pickText is null)
            return (false, $"{name} 콤보에 '{target}' 옵션이 없습니다 (있는 옵션: {string.Join(" / ", options)})");

        // 재렌더 대비 콤보 재탐색 후 선택
        (combos, labels) = await ReadComboLabelsAsync();
        (gi, ci, si) = Core.SubjectComboClassifier.ClassifyNarrativeAxis(labels);
        idx = axis switch { Axis.Grade => gi, Axis.Class => ci, _ => si };
        if (idx < 0) return (false, $"{name} 콤보 재탐색 실패");
        var pick = await _combo!.OpenAndPickAsync(combos.Nth(idx), pickText);
        if (!pick.Ok) return (false, $"{name} 선택 실패: {pick.Reason}");
        progress?.Report(new($"{name} → {pickText} 선택됨"));
        return (true, "");
    }

    /// <summary>조회조건 콤보를 목표 숫자로 맞춘다. 이미 맞으면 Why="skip" 으로 건너뜀.
    /// 옵션 텍스트가 "5"/"5학년" 어느 쪽이든 숫자만 비교해 매칭한다.</summary>
    private async Task<(bool Ok, string Why)> PickQueryComboAsync(
        string key, int number, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        // "학년" 라벨은 화면에 둘(학년도·학년) — 값이 학년(1~6)인 콤보를 고른다 (실측 버그 대응)
        Func<string, bool>? prefer = key == "학년"
            ? v => int.TryParse(DigitsOf(v), out var g) && g is >= 1 and <= 6
            : null;

        var (combo, current) = await FindQueryComboAsync(key, prefer);
        if (combo is null)
            return (false, $"{key} 콤보를 화면에서 찾지 못했습니다 (교과별 평가 화면인지 확인)");

        if (DigitsOf(current) == number.ToString())
            return (true, "skip");   // 이미 목표값

        // 옵션을 읽어 숫자가 일치하는 실제 표시 텍스트를 찾는다
        var options = await _combo!.OpenAndReadOptionsAsync(combo);
        var target = options.FirstOrDefault(o => DigitsOf(o) == number.ToString());
        if (target is null)
            return (false, $"{key} 콤보에 '{number}' 옵션이 없습니다 (있는 옵션: {string.Join(" / ", options)})");

        // 팝업이 닫힌 뒤 콤보 재탐색(재렌더 대비) 후 선택
        (combo, _) = await FindQueryComboAsync(key, prefer);
        if (combo is null) return (false, $"{key} 콤보 재탐색 실패");
        var pick = await _combo!.OpenAndPickAsync(combo, target);
        if (!pick.Ok) return (false, $"{key} 선택 실패: {pick.Reason}");
        progress?.Report(new($"{key} → {target} 선택됨"));
        return (true, "");
    }

    /// <summary>[조회] 버튼을 눌러 명단·그리드를 불러온다 (전담: 이동 후 콤보가 기본값과 같아
    /// 조회가 생략됐을 때 명시적으로 부른다). 그리드가 뜨면 성공.</summary>
    public async Task<(bool Ok, string Why)> QueryAsync(CancellationToken ct = default)
    {
        var page = RequirePage();
        await DismissAlertsAsync(ct);   // 남은 알림이 조회 클릭을 막지 않게
        var query = await FindButtonAsync(NeisSelectors.QueryButtonName);
        if (query is null) return (false, "[조회] 버튼을 찾지 못했습니다");
        await query.ClickAsync();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct);
            try { if (await FindGridAsync(page) is not null) { await Task.Delay(400, ct); return (true, ""); } }
            catch { /* 갱신 중 일시 오류 무시 */ }
        }
        return (false, "조회 후 그리드를 확인하지 못했습니다");
    }

    public async Task<(bool Ok, string Why)> SaveScreenAsync(CancellationToken ct = default)
    {
        var save = await FindButtonAsync(NeisSelectors.SaveButtonName);
        if (save is null) return (false, "[저장] 버튼을 찾지 못했습니다");

        // 실측: 저장할 변경이 없으면 버튼에 cl-disabled 가 붙는다 — 이미 같은 값이면 정상이므로
        // 실패가 아니라 '저장 생략' 성공으로 보고 배치가 다음으로 넘어가게 한다.
        var cls = await save.GetAttributeAsync("class") ?? "";
        if (cls.Contains(NeisSelectors.DisabledClass))
            return (true, "변경 없음 — 저장 생략");

        await save.ClickAsync();

        // 확인("저장하시겠습니까?") → 완료("저장되었습니다") 대화상자 순서로 최대 2개 처리.
        // 첫 대화상자는 4초, 그 다음부터는 1.5초만 — 없는 두 번째를 6초씩 기다리던 게 저장 지연의 주범.
        bool anyDialog = false;
        for (int d = 0; d < 2; d++)
        {
            var yes = await WaitDialogYesButtonAsync(TimeSpan.FromSeconds(d == 0 ? 4 : 1.5), ct);
            if (yes is null) break;
            anyDialog = true;
            await yes.ClickAsync();
            await Task.Delay(400, ct);
        }
        // 잔여 알림("변경된 내용이 없습니다" 등)이 남아 있으면 마저 닫는다 — 다음 단계 클릭이 막히지 않게
        await DismissAlertsAsync(ct);
        // 대화상자가 하나도 없었어도 클릭 자체는 됐으므로 성공으로 본다 (화면별 차이 허용)
        return (true, anyDialog ? "" : "대화상자 없음");
    }

    /// <summary>접근성 이름에 label 이 들어가는 보이는 버튼. GetByRole 우선, 실패 시 수동 스캔.</summary>
    private async Task<ILocator?> FindButtonAsync(string label)
    {
        var page = RequirePage();
        // 1) 접근성 이름 '정확히' 일치 우선 (예: "조회" ≠ "조회 숨김")
        try
        {
            var byRole = page.GetByRole(AriaRole.Button, new() { Name = label, Exact = true }).First;
            if (await byRole.IsVisibleAsync()) return byRole;
        }
        catch { /* 접근성 이름 불일치 → 수동 스캔 */ }

        // 2) 수동 스캔 — 정확 일치 즉시 반환, 없으면 '포함'을 폴백으로(단 토글류 '숨김/펼침' 제외)
        var candidates = page.Locator(NeisSelectors.AnyButton);
        int n = await candidates.CountAsync();
        ILocator? contains = null;
        for (int i = 0; i < n; i++)
        {
            var b = candidates.Nth(i);
            try
            {
                if (!await b.IsVisibleAsync()) continue;
                var name = await b.GetAttributeAsync("aria-label") ?? "";
                if (name == "") name = (await b.InnerTextAsync()).Trim();
                if (name == label) return b;   // 정확 일치 최우선
                if (contains is null && name.Contains(label) && !name.Contains("숨김") && !name.Contains("펼"))
                    contains = b;
            }
            catch { /* 스캔 중 사라진 요소는 건너뜀 */ }
        }
        return contains;
    }

    /// <summary>화면에 남아 있는 알림 대화상자("변경된 내용이 없습니다" 등)를 [확인]으로 닫는다.
    /// 모달이 남으면 이후 모든 클릭이 막혀 단계별 타임아웃이 연쇄돼 멈춘 것처럼 보인다 (실측 2026-07-19).
    /// 조회·저장·콤보 조작 등 상호작용 직전마다 불러 안전하게 비운다. 없으면 즉시 통과.</summary>
    private async Task DismissAlertsAsync(CancellationToken ct = default)
    {
        if (_page is null) return;
        for (int i = 0; i < 3; i++)   // 겹친 대화상자 대비 최대 3개
        {
            var yes = await WaitDialogYesButtonAsync(TimeSpan.FromMilliseconds(250), ct);
            if (yes is null) return;
            try { await yes.ClickAsync(new LocatorClickOptions { Timeout = 2000 }); } catch { return; }
            await Task.Delay(300, ct);
        }
    }

    /// <summary>대화상자 안의 긍정(확인/예) 버튼이 나타나면 반환. 시간 내 없으면 null.</summary>
    private async Task<ILocator?> WaitDialogYesButtonAsync(TimeSpan timeout, CancellationToken ct)
    {
        var page = RequirePage();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var buttons = page.Locator(NeisSelectors.DialogButton);
            int n = 0;
            try { n = await buttons.CountAsync(); } catch { }
            for (int i = 0; i < n; i++)
            {
                var b = buttons.Nth(i);
                try
                {
                    if (!await b.IsVisibleAsync()) continue;
                    var name = await b.GetAttributeAsync("aria-label") ?? "";
                    if (name == "") name = (await b.InnerTextAsync()).Trim();
                    if (NeisSelectors.DialogYesNames.Any(y => name.Contains(y))) return b;
                }
                catch { }
            }
            await Task.Delay(300, ct);
        }
        return null;
    }

    public async Task<RunReport> RunSubjectAsync(
        SubjectSheet sheet,
        GradeScale scale,
        bool dryRun,
        IProgress<ProgressInfo> progress,
        Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null,
        CancellationToken ct = default)
    {
        var page = RequirePage();

        // 화면 과목 확인 — 콜백이 없으면 예전처럼 즉시 차단, 있으면 UI 가 결정
        var subject = await GetCurrentSubjectAsync(ct);
        if (resolveMatch is null && subject != sheet.SubjectName)
            throw new InvalidOperationException(
                $"화면 교과 '{subject}' ≠ 대상 '{sheet.SubjectName}'. 나이스에서 '{sheet.SubjectName}'를 조회하세요.");

        // 조회된 그리드에 쓰는 일은 GradeWriter 전담 (NarrativeWriter 와 대칭, R11)
        var grid = await FindGridAsync(page)
            ?? throw new InvalidOperationException("평가 그리드를 찾지 못했습니다. [조회]를 눌렀는지 확인하세요.");
        return await new GradeWriter(page, _scroller!, _combo!).RunAsync(
            grid, sheet, scale, subject, dryRun, progress, resolveMatch, ct);
    }

    private static async Task<ILocator?> FindGridAsync(IPage page)
    {
        // 성적(colcount=8) 그리드 우선 — 있으면 그걸로
        var grids = page.Locator(NeisSelectors.Grid);
        int n = await grids.CountAsync();
        for (int i = 0; i < n; i++)
            if (await grids.Nth(i).IsVisibleAsync()) return grids.Nth(i);

        // 폴백: colcount 무관하게 보이는 CLX 그리드 아무거나 (학기말종합의견=5 등 다른 화면 대응)
        var any = page.Locator("div.cl-grid[role='grid']");
        n = await any.CountAsync();
        for (int i = 0; i < n; i++)
            if (await any.Nth(i).IsVisibleAsync()) return any.Nth(i);
        return null;
    }

    public async Task<string> InspectDomAsync(CancellationToken ct = default)
    {
        var page = RequirePage();
        return await new DomInspector(page).InspectAsync();
    }

    public async Task<NarrativeReport> RunNarrativesAsync(
        string subjectName,
        IReadOnlyList<NarrativeEntry> entries,
        bool dryRun,
        int maxBytes,
        IProgress<ProgressInfo> progress,
        CancellationToken ct = default,
        Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null)
    {
        var page = RequirePage();
        void Log(string m) => progress.Report(new ProgressInfo(m));

        // 안전장치: 과목 콤보가 있으면 반드시 일치해야 함. 없으면(화면 구조 미확정) 경고 후 진행.
        // (과목 다름은 아래 확인 창에서 사용자가 동의할 수 있으므로, 콜백이 있으면 던지지 않는다)
        var subject = await GetCurrentSubjectAsync(ct);
        if (resolveMatch is null && subject is not null && subject != subjectName)
            throw new InvalidOperationException(
                $"화면 교과 '{subject}' ≠ 대상 '{subjectName}'. 나이스에서 '{subjectName}'를 조회하세요.");
        if (subject is null)
            Log($"⚠ 화면에서 교과 콤보를 찾지 못했습니다. 현재 화면이 '{subjectName}' 대상인지 직접 확인하세요.");

        // 확인 창 콜백(등급과 동일 타입)을 서술문 매칭용 nameMap 으로 어댑트
        Func<IReadOnlyDictionary<int, (string? No, string? Name)>,
             Task<IReadOnlyDictionary<string, string>?>>? resolveNameMap = null;
        if (resolveMatch is not null)
        {
            resolveNameMap = async rowMap =>
            {
                var meta = rowMap.ToDictionary(kv => kv.Key,
                    kv => new RowMeta(kv.Value.No, kv.Value.Name, null));
                var decision = await resolveMatch(
                    new MatchContext(subject, subjectName, meta, Array.Empty<int>()));
                if (decision is null) return null;   // 취소
                return decision.NameMap ?? new Dictionary<string, string>();
            };
        }

        var writer = new NarrativeWriter(page, _scroller!);
        return await writer.RunAsync(entries, dryRun, maxBytes, Log,
            (i, t) => progress.Report(new ProgressInfo("", i, t)), ct, resolveNameMap);
    }

    private IPage RequirePage() =>
        _page ?? throw new InvalidOperationException("브라우저에 연결되지 않았습니다. 먼저 연결하세요.");

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.CloseAsync();
        _pw?.Dispose();
    }
}
