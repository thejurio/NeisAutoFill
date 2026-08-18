using System.Text;
using Microsoft.Playwright;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.Automation;

/// <summary>
/// 학급시간표관리 화면의 <b>비식별</b> 진단 리포트 (실기기검증 §9, 로드맵 T4).
///
/// 나이스가 개편되면 여기부터 찍어 무엇이 달라졌는지 본다. 셀렉터 문자열을 먼저 고치지 않는다.
/// <b>교사명·계정 ID 는 가상값(교사A / account-a)으로 치환</b>해 그대로 공유·커밋할 수 있게 만든다(D-012).
/// 읽기 전용 — 메뉴를 열면 반드시 [취소]로 닫는다.
/// </summary>
public sealed class TimetableDiagnostics(IPage page)
{
    public async Task<string> CaptureAsync()
    {
        var reader = new TimetableReader(page);
        var mask = new Anonymizer();
        var sb = new StringBuilder();

        sb.AppendLine("# 연간 시간표 진단 리포트 (비식별)");
        sb.AppendLine($"수집 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // ── 화면 ──────────────────────────────────────────────
        sb.AppendLine("## 화면");
        var titles = await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('div.app-tit')]
                .filter(t => { const r = t.getBoundingClientRect(); return r.width > 0 && r.height > 0; })
                .map(t => (t.innerText || '').trim())");
        sb.AppendLine($"- 보이는 제목: {(titles.Length > 0 ? string.Join(" / ", titles) : "(없음)")}");
        sb.AppendLine($"- 시간표 화면 인식: {await reader.IsTimetableScreenAsync()}");
        sb.AppendLine($"- 그리드 렌더됨: {await reader.HasGridAsync()}");

        var appIds = await page.EvaluateAsync<string[]>(@"() => {
          try {
            return cpr.core.Platform.INSTANCE.getAllRunningAppInstances()
              .map(a => { try { return a.getAppId ? a.getAppId() : (a.id || '?'); } catch (e) { return '?'; } })
              .filter(id => /tm|시간표/i.test(id));
          } catch (e) { return ['no-cpr']; }
        }");
        sb.AppendLine($"- 시간표 앱 ID: {(appIds.Length > 0 ? string.Join(", ", appIds) : "(없음)")}");
        sb.AppendLine();

        // ── CLX 그리드 ────────────────────────────────────────
        sb.AppendLine("## CLX 그리드");
        var grids = await page.EvaluateAsync<string[]>(@"() => {
          if (typeof cpr === 'undefined') return ['no-cpr'];
          const seen = new Set(); const found = [];
          const visit = c => {
            if (!c || seen.has(c)) return; seen.add(c);
            try { if (c.type === 'grid') found.push(c); } catch (e) {}
            try { if (c.getChildren) c.getChildren().forEach(visit); } catch (e) {}
            try { if (c.content && c.content.getChildren) c.content.getChildren().forEach(visit); } catch (e) {}
            try { if (c.getEmbeddedAppInstance) { const ea = c.getEmbeddedAppInstance(); if (ea && ea.getContainer) visit(ea.getContainer()); } } catch (e) {}
          };
          cpr.core.Platform.INSTANCE.getAllRunningAppInstances().forEach(a => { try { visit(a.getContainer()); } catch (e) {} });
          return found.filter(g => { try { return g.getDataRowCount && g.getDataRowCount() > 0; } catch (e) { return true; } }).map(g => {
            let id = '?', rows = -1, fields = [];
            try { id = g.id || '?'; } catch (e) {}
            try { rows = g.getDataRowCount ? g.getDataRowCount() : -1; } catch (e) {}
            try {
              const r = g.getDataRow(0);
              let src = null;
              try { if (typeof r.toObject === 'function') src = r.toObject(); } catch (e) {}
              if (!src) { try { if (typeof r.getRowData === 'function') src = r.getRowData(); } catch (e) {} }
              if (!src) { try { src = JSON.parse(JSON.stringify(r)); } catch (e) {} }
              if (src) fields = Object.keys(src).filter(k => k !== '$id').slice(0, 20);
            } catch (e) {}
            return id + ' | 행 ' + rows + ' | 필드 ' + fields.join(',');
          });
        }");
        foreach (var g in grids) sb.AppendLine($"- {g}");
        sb.AppendLine();

        // ── 주차 ──────────────────────────────────────────────
        sb.AppendLine("## 주차");
        try
        {
            var weeks = await reader.ReadWeeksAsync();
            sb.AppendLine($"- 주차 수: {weeks.Count}");
            foreach (var w in weeks.Take(3))
                sb.AppendLine($"  - [{w.Index}] {w.Start:yyyy-MM-dd}~{w.End:MM-dd} · {w.Name} · 행사={w.Event}");
        }
        catch (Exception ex) { sb.AppendLine($"- 읽기 실패: {ex.Message}"); }
        sb.AppendLine();

        // ── 현재 주 셀 ────────────────────────────────────────
        sb.AppendLine("## 현재 주 셀");
        try
        {
            var snap = await reader.ReadCurrentWeekAsync();
            sb.AppendLine($"- 셀 {snap.Cells.Count}칸 · 날짜 {snap.Dates.Count}일 · 경고 {snap.Warnings.Count}건");
            if (snap.Dates.Count > 0)
                sb.AppendLine($"- 날짜 범위: {snap.Dates[0]:MM-dd} ~ {snap.Dates[^1]:MM-dd}");
            foreach (var w in snap.Warnings.Take(5)) sb.AppendLine($"  ⚠ {w}");

            foreach (var (cell, text) in snap.Cells.Where(c => c.Value.Length > 0).Take(5))
                sb.AppendLine($"  - {cell} = {mask.Apply(text).Replace("\n", "⏎")}");
        }
        catch (Exception ex) { sb.AppendLine($"- 읽기 실패: {ex.Message}"); }
        sb.AppendLine();

        // ── 카탈로그 ──────────────────────────────────────────
        sb.AppendLine("## 카탈로그 (우클릭 메뉴)");
        TimetableCatalog? catalog = null;
        for (var col = 1; col <= 5 && catalog is null; col++)
        {
            try { catalog = await reader.ReadCatalogAsync(0, col); }
            catch (Exception ex) { sb.AppendLine($"- {col}열 실패: {ex.Message}"); break; }
            if (catalog is null) sb.AppendLine($"- {col}열: 메뉴 안 열림 (수업 없는 날/학기 시작 전)");
        }

        if (catalog is null)
        {
            sb.AppendLine("- 메뉴를 열 수 있는 셀이 없어 카탈로그를 읽지 못했습니다.");
        }
        else
        {
            sb.AppendLine($"- 전체 {catalog.All.Count} · 후보 {catalog.Assignable.Count} · 명령 {catalog.Commands.Count} · 미해석 {catalog.Unknown.Count}");
            sb.AppendLine($"- 지문: {catalog.Fingerprint}");
            sb.AppendLine("- 후보:");
            foreach (var o in catalog.Assignable)
                sb.AppendLine($"  - [{o.Kind}] {o.Subject} / {mask.Teacher(o.TeacherName)} / {mask.Account(o.TeacherAccount)}");
            sb.AppendLine("- 명령: " + string.Join(", ", catalog.Commands.Select(o => o.RawText)));
            foreach (var o in catalog.Unknown)
                sb.AppendLine($"  ⚠ 미해석: {mask.Apply(o.RawText)}");
        }

        return sb.ToString();
    }

    /// <summary>교사명·계정을 가상값으로 바꾼다. 같은 값은 같은 별칭이 되어 구조는 그대로 남는다.</summary>
    private sealed class Anonymizer
    {
        private readonly Dictionary<string, string> _teachers = new();
        private readonly Dictionary<string, string> _accounts = new();

        public string Teacher(string name) => Map(_teachers, name, i => $"교사{(char)('A' + i % 26)}");
        public string Account(string account) => Map(_accounts, account, i => $"account-{(char)('a' + i % 26)}");

        /// <summary>"과목(교사(계정))" 형태의 문자열에서 교사·계정만 치환한다.</summary>
        public string Apply(string text)
        {
            var parsed = TimetableMenuParser.Parse(text);
            if (parsed.TeacherName.Length == 0) return text;
            return text.Replace(parsed.TeacherName, Teacher(parsed.TeacherName))
                       .Replace(parsed.TeacherAccount, Account(parsed.TeacherAccount));
        }

        private static string Map(Dictionary<string, string> dict, string key, Func<int, string> make)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (!dict.TryGetValue(key, out var alias)) dict[key] = alias = make(dict.Count);
            return alias;
        }
    }
}
