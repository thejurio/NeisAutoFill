using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NeisAutoFill.Automation;

/// <summary>
/// 화면에서 읽은 범위 정보 (원문). 저장·로그로 나가기 전에 <b>반드시 해시로 바꾼다</b>(D-012).
/// </summary>
/// <param name="Host">교육청 호스트 (개인정보 아님)</param>
/// <param name="School">학교명 원문 — 해시 계산에만 쓴다</param>
/// <param name="User">로그인 사용자 원문 — 해시 계산에만 쓴다</param>
public sealed record NeisScopeInfo(
    string Host, string School, string User,
    int SchoolYear, int Semester, int Grade, string ClassName)
{
    /// <summary>로그·화면에 보여도 되는 부분만.</summary>
    public string SafeDescribe() => $"{SchoolYear}학년도 {Semester}학기 {Grade}학년 {ClassName}반";
}

/// <summary>
/// 학급시간표관리 화면의 조회 조건과 학교·사용자 식별값을 읽는다 (기술설계 §13).
///
/// 실측(2026-08-18): 조회 조건은 콤보의 <c>aria-label="학년도, 2026"</c> 형식이고,
/// 학교명과 사용자는 헤더의 별도 요소에 각각 들어 있다.
/// <b>uuid-* id 는 쓰지 않는다</b>(D-004) — 텍스트 형태로 찾는다.
/// </summary>
public sealed partial class TimetableScopeReader(IPage page)
{
    [GeneratedRegex(@"^(.+?)\s*,\s*(.+)$")]
    private static partial Regex AriaPair();

    /// <summary>범위를 읽는다. 조회 조건을 못 찾으면 null.</summary>
    public async Task<NeisScopeInfo?> ReadAsync()
    {
        var combos = await page.EvaluateAsync<string[]>(
            @"() => [...document.querySelectorAll('[role=combobox]')]
                .filter(c => { const r = c.getBoundingClientRect(); return r.width > 0 && r.height > 0; })
                .map(c => c.getAttribute('aria-label') || '')
                .filter(a => a.length > 0)");

        var values = new Dictionary<string, string>();
        foreach (var aria in combos)
        {
            var m = AriaPair().Match(aria);
            if (!m.Success) continue;
            var key = m.Groups[1].Value.Trim();
            // 같은 이름이 여러 번 나오면 첫 값을 쓴다 ("학년도" 가 안내 문구로도 나온다)
            if (!values.ContainsKey(key)) values[key] = m.Groups[2].Value.Trim();
        }

        if (!TryInt(values, "학년도", out var year) || !TryInt(values, "학기", out var semester)
            || !TryInt(values, "학년", out var grade))
            return null;

        values.TryGetValue("반", out var className);

        var (school, user) = await ReadIdentityAsync();
        var host = new Uri(page.Url).Host;

        return new NeisScopeInfo(host, school, user, year, semester, grade, className ?? "");
    }

    /// <summary>헤더에서 학교명과 사용자를 찾는다. 못 찾으면 빈 문자열 — 범위가 덜 구체적일 뿐 동작은 한다.</summary>
    private async Task<(string School, string User)> ReadIdentityAsync()
    {
        var found = await page.EvaluateAsync<string[]>(@"() => {
          const vis = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const texts = [...document.querySelectorAll('div, span, strong')]
            .filter(vis)
            .map(e => (e.innerText || '').trim())
            .filter(t => t.length > 0 && t.length < 40 && !t.includes('\n'));

          const school = texts.find(t => /(초등학교|중학교|고등학교|학교)$/.test(t)) || '';
          const user = texts.find(t => /^[^()]{2,10}\([A-Za-z0-9_.#-]{2,}\)$/.test(t)) || '';
          return [school, user];
        }");

        return (found.ElementAtOrDefault(0) ?? "", found.ElementAtOrDefault(1) ?? "");
    }

    private static bool TryInt(IReadOnlyDictionary<string, string> values, string key, out int result)
    {
        result = 0;
        if (!values.TryGetValue(key, out var raw)) return false;
        var digits = new string(raw.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out result);
    }
}
