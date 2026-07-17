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
    public async Task<IReadOnlyList<SubjectPlan>> ImportAsync(
        string filePath, GradeScale scale, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.GasUrl))
            throw new InvalidOperationException("생성기 서버(GAS) URL 이 설정되지 않았습니다.");

        // 추출은 별도 STA 스레드에서 — 한컴 COM(HWP 변환)이 STA 필수, UI 도 안 멈춤
        progress?.Report("문서 내용 추출 중...");
        var extraction = await RunStaAsync(() => PlanFileExtractor.Extract(filePath));

        progress?.Report("문서의 과목 목록 확인 중...");
        IReadOnlyList<string> subjects;
        try
        {
            subjects = await ListSubjectsAsync(extraction, ct);
        }
        catch
        {
            subjects = Array.Empty<string>();   // 목록 실패 → 통짜 파싱 폴백
        }

        if (subjects.Count == 0)
        {
            progress?.Report("전체 문서 분석 중...");
            return await ParseSubjectAsync(extraction, scale, onlySubject: null, ct);
        }

        var plans = new List<SubjectPlan>();
        var failedSubjects = new List<string>();
        for (int i = 0; i < subjects.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"'{subjects[i]}' 분석 중... ({i + 1}/{subjects.Count} 과목)");
            try
            {
                plans.AddRange(await ParseSubjectAsync(extraction, scale, subjects[i], ct));
            }
            catch
            {
                failedSubjects.Add(subjects[i]);   // 한 과목 실패해도 나머지는 계속
            }
        }

        if (plans.Count == 0)
            throw new InvalidOperationException("문서에서 평가계획을 인식하지 못했습니다. 파일이 평가기준이 담긴 계획서인지 확인해 주세요.");
        if (failedSubjects.Count > 0)
            progress?.Report($"⚠ 일부 과목 인식 실패: {string.Join(", ", failedSubjects)} — 나머지는 정상 인식됨");
        return plans;
    }

    private async Task<IReadOnlyList<string>> ListSubjectsAsync(
        PlanFileExtractor.Extraction extraction, CancellationToken ct)
    {
        var payload = new
        {
            action = "listPlanSubjects",
            clientName = $"NeisAutoFill ({Environment.UserName})",
            pdfBase64 = extraction.PdfBase64,
            text = extraction.Text,
        };
        using var response = await http.PostAsJsonAsync(options.GasUrl, payload, Json, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<GasListResponse>(body, Json);
        if (result is null || !result.Ok || result.Subjects is null) return Array.Empty<string>();
        // 창의적 체험활동류는 클라이언트에서도 한 번 더 거른다
        return result.Subjects
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !s.Contains("창의적") && !s.Contains("창체"))
            .Distinct().ToList();
    }

    private async Task<IReadOnlyList<SubjectPlan>> ParseSubjectAsync(
        PlanFileExtractor.Extraction extraction, GradeScale scale, string? onlySubject, CancellationToken ct)
    {
        var payload = new
        {
            action = "parsePlan",
            clientName = $"NeisAutoFill ({Environment.UserName})",
            scaleLabels = scale.Levels.Select(l => l.Label).ToList(),
            onlySubject,
            pdfBase64 = extraction.PdfBase64,
            text = extraction.Text,
        };
        using var response = await http.PostAsJsonAsync(options.GasUrl, payload, Json, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
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

    private sealed record GasListResponse(bool Ok, string? Error, List<string>? Subjects);
    private sealed record GasParseResponse(bool Ok, string? Error, List<GasSubject>? Subjects);
    private sealed record GasSubject(string? SubjectName, List<GasDomain>? Domains);
    private sealed record GasDomain(string? DomainName, string? Achievement, Dictionary<string, string>? Criteria);
}
