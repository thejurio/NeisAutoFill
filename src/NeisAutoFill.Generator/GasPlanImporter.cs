using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Generator;

/// <summary>
/// 평가계획서 파일 → GAS parsePlan → SubjectPlan 목록.
/// 이지에듀/스쿨마스터 등 양식 구분 없이 범용 프롬프트 하나로 처리 (GAS 쪽 parsePlanDocument).
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]   // 추출기(한컴 COM) 의존
public sealed class GasPlanImporter(HttpClient http, GeneratorOptions options)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// 2단계 파싱: ① 과목 목록만 빠르게 인식 → ② 과목별로 나눠 각각 완전 추출.
    /// 과목당 출력이 작아져 대형 문서(스쿨마스터 등)에서도 영역 누락·잘림이 없다.
    /// 과목 목록 인식이 실패하면 통짜 파싱으로 폴백.
    /// </summary>
    /// <param name="selectSubjects">
    /// 과목 목록 인식 후, 실제로 불러올 과목을 사용자가 고르는 콜백 (담임 F9 M4b).
    /// null 반환 = 취소(OperationCanceledException). null 콜백이면 전부 불러온다(기존 동작).
    /// </param>
    public async Task<IReadOnlyList<SubjectPlan>> ImportAsync(
        string filePath, GradeScale scale, IProgress<string>? progress = null,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<string>?>>? selectSubjects = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.GasUrl))
            throw new InvalidOperationException("생성기 서버(GAS) URL 이 설정되지 않았습니다.");

        // 추출은 별도 STA 스레드에서 — 한컴 COM(HWP 변환)이 STA 필수, UI 도 안 멈춤
        progress?.Report("문서 내용 추출 중...");
        var extraction = await RunStaAsync(() => PlanFileExtractor.Extract(filePath));

        // 이번 인식에서 실제로 쓴 API 키(뒤 4자리)들 — 요약 로그 F열에 모아 넣는다
        var usedKeys = new SortedSet<string>(StringComparer.Ordinal);

        progress?.Report("문서의 과목 목록 확인 중...");
        IReadOnlyList<string> subjects;
        try
        {
            subjects = await ListSubjectsAsync(extraction, usedKeys, ct);
        }
        catch
        {
            subjects = Array.Empty<string>();   // 목록 실패 → 통짜 파싱 폴백
        }

        // 사용자가 불러올 과목을 고른다 (목록을 인식했을 때만 — 폴백 통짜 파싱은 선택 불가)
        if (subjects.Count > 0 && selectSubjects is not null)
        {
            var chosen = await selectSubjects(subjects);
            if (chosen is null) throw new OperationCanceledException("사용자가 과목 선택을 취소했습니다.");
            subjects = chosen.Where(subjects.Contains).Distinct().ToList();
            if (subjects.Count == 0) throw new OperationCanceledException("선택된 과목이 없습니다.");
        }

        if (subjects.Count == 0)
        {
            progress?.Report("전체 문서 분석 중...");
            try
            {
                var whole = await ParseSubjectAsync(extraction, scale, onlySubject: null, usedKeys, ct);
                await LogImportAsync("SUCCESS", $"{whole.Count}과목 인식 (통짜)", usedKeys, ct);
                return whole;
            }
            catch
            {
                await LogImportAsync("FAIL", "과목 목록·통짜 인식 모두 실패", usedKeys, ct);
                throw;
            }
        }

        var plans = new List<SubjectPlan>();
        var failedAt = new List<int>();   // 실패한 과목의 순번(1-based)
        for (int i = 0; i < subjects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"'{subjects[i]}' 분석 중... ({i + 1}/{subjects.Count} 과목)");
            try
            {
                plans.AddRange(await ParseSubjectAsync(extraction, scale, subjects[i], usedKeys, ct));
            }
            catch
            {
                failedAt.Add(i + 1);   // 한 과목 실패해도 나머지는 계속
            }
        }

        // 사용 기록: 과목별이 아니라 전체 1건
        if (failedAt.Count == 0)
            await LogImportAsync("SUCCESS", $"{subjects.Count}과목 인식", usedKeys, ct);
        else
            await LogImportAsync("FAIL",
                $"{subjects.Count - failedAt.Count}/{subjects.Count} 인식, " +
                $"{string.Join("·", failedAt)}번째 실패", usedKeys, ct);

        if (plans.Count == 0)
            throw new InvalidOperationException("문서에서 평가계획을 인식하지 못했습니다. 파일이 평가기준이 담긴 계획서인지 확인해 주세요.");
        if (failedAt.Count > 0)
            progress?.Report($"⚠ {subjects.Count}과목 중 {failedAt.Count}과목 인식 실패 — 나머지는 정상 인식됨");
        return plans;
    }

    /// <summary>전담 import 결과 — 한 학년치 평가계획.</summary>
    public sealed record GradePlanSet(int Grade, IReadOnlyList<SubjectPlan> Plans);

    /// <summary>
    /// 전담 통합 분석 (F9 M4b): 한 파일에서 (학년·과목) 단위를 인식 → 사용자가 고름 →
    /// 단위별로 완전 추출 → 학년별로 묶어 반환. 담임 ImportAsync 와 비슷하지만 '학년' 축이 추가된다.
    /// </summary>
    /// <param name="selectUnits">인식된 (학년·과목) 단위 중 불러올 것을 사용자가 고르는 콜백.
    /// 학년 불명(Grade=0) 단위는 사용자가 학년을 채워 돌려준다. null 반환 = 취소.</param>
    public async Task<IReadOnlyList<GradePlanSet>> ImportUnitsAsync(
        string filePath, GradeScale scale, IProgress<string>? progress,
        Func<IReadOnlyList<Core.PlanUnit>, Task<IReadOnlyList<Core.PlanUnit>?>>? selectUnits,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.GasUrl))
            throw new InvalidOperationException("생성기 서버(GAS) URL 이 설정되지 않았습니다.");

        progress?.Report("문서 내용 추출 중...");
        var extraction = await RunStaAsync(() => PlanFileExtractor.Extract(filePath));
        var usedKeys = new SortedSet<string>(StringComparer.Ordinal);

        progress?.Report("문서의 학년·과목 확인 중...");
        IReadOnlyList<Core.PlanUnit> units;
        try { units = await ListUnitsAsync(extraction, usedKeys, ct); }
        catch { units = Array.Empty<Core.PlanUnit>(); }

        if (units.Count == 0)
            throw new InvalidOperationException(
                "문서에서 (학년·과목)을 인식하지 못했습니다.\n" +
                "GAS 웹앱이 최신 code.gs(listPlanUnits 지원)로 재배포되었는지, 파일이 평가계획서인지 확인해 주세요.");

        // 사용자가 불러올 단위를 고르고, 학년 불명은 채워 온다
        if (selectUnits is not null)
        {
            var chosen = await selectUnits(units);
            if (chosen is null) throw new OperationCanceledException("사용자가 학년·과목 선택을 취소했습니다.");
            units = chosen.Where(u => u.HasGrade && !string.IsNullOrWhiteSpace(u.Subject)).ToList();
            if (units.Count == 0) throw new OperationCanceledException("선택된 학년·과목이 없습니다.");
        }

        // 단위별 완전 추출 (그 학년의 그 과목만) — 학년별로 묶는다
        var byGrade = new Dictionary<int, List<SubjectPlan>>();
        var failed = new List<string>();
        for (int i = 0; i < units.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var u = units[i];
            progress?.Report($"'{u.Display}' 분석 중... ({i + 1}/{units.Count})");
            try
            {
                var plans = await ParseSubjectAsync(extraction, scale, u.Subject, usedKeys, ct, onlyGrade: u.Grade);
                if (!byGrade.TryGetValue(u.Grade, out var list)) byGrade[u.Grade] = list = new();
                list.AddRange(plans);
            }
            catch { failed.Add(u.Display); }
        }

        if (byGrade.Count == 0)
        {
            await LogImportAsync("FAIL", $"전담 {units.Count}단위 인식 모두 실패", usedKeys, ct);
            throw new InvalidOperationException("선택한 학년·과목에서 평가계획을 인식하지 못했습니다.");
        }
        if (failed.Count > 0)
        {
            progress?.Report($"⚠ {units.Count}단위 중 {failed.Count}개 인식 실패 — 나머지는 정상");
            await LogImportAsync("FAIL", $"전담 {units.Count - failed.Count}/{units.Count} 인식, 실패: {string.Join("·", failed)}", usedKeys, ct);
        }
        else await LogImportAsync("SUCCESS", $"전담 {units.Count}단위 인식", usedKeys, ct);

        return byGrade.OrderBy(kv => kv.Key)
            .Select(kv => new GradePlanSet(kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>문서에서 (학년·과목) 단위 목록 인식 (전담 F9 M4b — GAS listPlanUnits).</summary>
    private async Task<IReadOnlyList<Core.PlanUnit>> ListUnitsAsync(
        PlanFileExtractor.Extraction extraction, ISet<string> usedKeys, CancellationToken ct)
    {
        var (ts, nonce, sig) = GasAuth.Sign("listPlanUnits");
        var payload = new
        {
            action = "listPlanUnits",
            authVersion = "2", timestamp = ts, nonce, signature = sig,
            clientName = $"NeisAutoFill ({Environment.UserName})",
            pdfBase64 = extraction.PdfBase64,
            text = extraction.Text,
        };
        using var response = await http.PostAsJsonAsync(options.GasUrl, payload, Json, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<GasUnitsResponse>(body, Json);
        if (result is not null && !string.IsNullOrEmpty(result.KeyHint)) usedKeys.Add(result.KeyHint);
        return ParseUnitsResponse(body);
    }

    /// <summary>listPlanUnits 응답 JSON → (학년·과목) 단위 목록 (테스트 가능하도록 분리, F9 M4b).
    /// 창체류 제외, 학년 1~6 아니면 0(불명), 과목 공백 제외, 중복 제거.</summary>
    public static IReadOnlyList<Core.PlanUnit> ParseUnitsResponse(string body)
    {
        GasUnitsResponse? result;
        try { result = JsonSerializer.Deserialize<GasUnitsResponse>(body, Json); }
        catch (JsonException) { return Array.Empty<Core.PlanUnit>(); }
        if (result is null || !result.Ok || result.Units is null) return Array.Empty<Core.PlanUnit>();
        return result.Units
            .Where(u => u is not null && !string.IsNullOrWhiteSpace(u.Subject))
            .Where(u => !u!.Subject!.Contains("창의적") && !u.Subject.Contains("창체"))
            .Select(u => new Core.PlanUnit(u!.Grade is >= 1 and <= 6 ? u.Grade : 0, u.Subject!.Trim()))
            .Distinct()
            .ToList();
    }

    /// <summary>평가계획 인식 전체 결과 1건 기록 (실패해도 무시 — 인식 자체엔 영향 없음).</summary>
    private async Task LogImportAsync(string result, string info, IReadOnlyCollection<string> usedKeys, CancellationToken ct)
    {
        try
        {
            var (ts, nonce, sig) = GasAuth.Sign("logPlanImport");
            var payload = new
            {
                action = "logPlanImport",
                authVersion = "2", timestamp = ts, nonce, signature = sig,
                clientName = $"NeisAutoFill ({Environment.UserName})",
                result,
                info,
                keyHint = string.Join(",", usedKeys),   // 실제로 쓴 키 뒤 4자리 → F열
            };
            using var _ = await http.PostAsJsonAsync(options.GasUrl, payload, Json, ct);
        }
        catch { /* 기록 실패는 무시 */ }
    }

    private async Task<IReadOnlyList<string>> ListSubjectsAsync(
        PlanFileExtractor.Extraction extraction, ISet<string> usedKeys, CancellationToken ct)
    {
        var (ts, nonce, sig) = GasAuth.Sign("listPlanSubjects");
        var payload = new
        {
            action = "listPlanSubjects",
            authVersion = "2", timestamp = ts, nonce, signature = sig,
            clientName = $"NeisAutoFill ({Environment.UserName})",
            pdfBase64 = extraction.PdfBase64,
            text = extraction.Text,
        };
        using var response = await http.PostAsJsonAsync(options.GasUrl, payload, Json, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<GasListResponse>(body, Json);
        if (result is null || !result.Ok || result.Subjects is null) return Array.Empty<string>();
        if (!string.IsNullOrEmpty(result.KeyHint)) usedKeys.Add(result.KeyHint);
        // 창의적 체험활동류는 클라이언트에서도 한 번 더 거른다
        return result.Subjects
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !s.Contains("창의적") && !s.Contains("창체"))
            .Distinct().ToList();
    }

    private async Task<IReadOnlyList<SubjectPlan>> ParseSubjectAsync(
        PlanFileExtractor.Extraction extraction, GradeScale scale, string? onlySubject, ISet<string> usedKeys,
        CancellationToken ct, int onlyGrade = 0)
    {
        var (ts, nonce, sig) = GasAuth.Sign("parsePlan");
        var payload = new
        {
            action = "parsePlan",
            authVersion = "2", timestamp = ts, nonce, signature = sig,
            clientName = $"NeisAutoFill ({Environment.UserName})",
            scaleLabels = scale.Levels.Select(l => l.Label).ToList(),
            onlySubject,
            onlyGrade = onlyGrade >= 1 ? onlyGrade : (int?)null,   // 전담: 이 학년의 그 과목만 (F9 M4b)
            pdfBase64 = extraction.PdfBase64,
            text = extraction.Text,
        };
        using var response = await http.PostAsJsonAsync(options.GasUrl, payload, Json, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        // 인식에 쓴 키(F열용)를 먼저 뽑아두고 — 본 파싱은 예외를 던질 수 있으므로 그 전에
        var hint = JsonSerializer.Deserialize<GasParseResponse>(body, Json)?.KeyHint;
        if (!string.IsNullOrEmpty(hint)) usedKeys.Add(hint);
        return ParseResponse(body, scale);
    }

    /// <summary>GAS 응답 JSON → SubjectPlan 목록 (테스트 가능하도록 분리).</summary>
    public static IReadOnlyList<SubjectPlan> ParseResponse(string body, GradeScale scale)
    {
        GasParseResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<GasParseResponse>(body, Json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                "서버가 JSON 이 아닌 응답을 보냈습니다. GAS 웹앱이 최신 code.gs 로 재배포되었는지 확인하세요.");
        }
        if (result is null) throw new InvalidOperationException("서버 응답 파싱 실패");
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "서버 오류 (상세 없음)");
        if (result.Subjects is null)
            throw new InvalidOperationException(
                "서버가 평가계획 분석(parsePlan)을 지원하지 않습니다.\n" +
                "GAS 웹앱이 구버전입니다 — Apps Script 에 최신 code.gs 를 붙여넣고 [새 버전]으로 재배포해 주세요.");

        var labels = scale.Levels.Select(l => l.Label).ToHashSet();
        var plans = new List<SubjectPlan>();
        foreach (var subj in result.Subjects ?? new())
        {
            if (string.IsNullOrWhiteSpace(subj.SubjectName)) continue;
            var domains = new List<string>();
            var criteria = new Dictionary<(string, string), CriteriaEntry>();
            foreach (var d in subj.Domains ?? new())
            {
                var name = (d.DomainName ?? "").Trim();
                if (name == "") continue;
                // 같은 영역의 반복 평가는 별개 행으로 유지 — 표·엑셀 열 이름은 유일해야 하므로
                // 두 번째부터 (2), (3) 번호만 붙인다. 나이스 업로드 시엔 순서 매핑으로 처리.
                if (domains.Contains(name))
                {
                    int n = 2;
                    while (domains.Contains($"{name}({n})")) n++;
                    name = $"{name}({n})";
                }
                domains.Add(name);
                var ach = string.IsNullOrWhiteSpace(d.Achievement) ? null : d.Achievement.Trim();
                foreach (var (grade, text) in d.Criteria ?? new())
                {
                    if (!labels.Contains(grade) || string.IsNullOrWhiteSpace(text)) continue;
                    criteria[(name, grade)] = new CriteriaEntry(text.Trim(), ach);
                }
            }
            if (domains.Count > 0)
                plans.Add(new SubjectPlan(subj.SubjectName.Trim(), domains, criteria));
        }
        if (plans.Count == 0)
            throw new InvalidOperationException("문서에서 평가계획을 인식하지 못했습니다. 파일이 평가기준이 담긴 계획서인지 확인해 주세요.");
        return plans;
    }

    private static Task<T> RunStaAsync<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private sealed record GasListResponse(bool Ok, string? Error, List<string>? Subjects, string? KeyHint);
    private sealed record GasUnitsResponse(bool Ok, string? Error, List<GasUnit>? Units, string? KeyHint);
    private sealed record GasUnit(int Grade, string? Subject);
    private sealed record GasParseResponse(bool Ok, string? Error, List<GasSubject>? Subjects, string? KeyHint);
    private sealed record GasSubject(string? SubjectName, List<GasDomain>? Domains);
    private sealed record GasDomain(string? DomainName, string? Achievement, Dictionary<string, string>? Criteria);
}
