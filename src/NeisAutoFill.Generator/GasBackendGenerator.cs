using System.Net.Http.Json;
using System.Text.Json;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Generator;

/// <summary>
/// GAS 웹앱 doPost 호출 구현 (유일한 생성 경로).
/// API 키는 GAS 가 APIKeys 시트에서 꺼내 서버 쪽에서 Gemini 를 호출하므로
/// 프로그램 사용자는 키를 알 필요도, 입력할 필요도 없다.
/// 성취 내역 + 척도 뉘앙스 맵만 보내고 완성된 서술문을 받는다.
/// </summary>
public sealed class GasBackendGenerator(HttpClient http, GeneratorOptions options) : IEvaluationGenerator
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>직전 생성에서 GAS 가 실제로 쓴 API 키의 뒤 4자리 (배치 사용 로그 F열용). 없으면 빈 문자열.</summary>
    public string LastKeyHint { get; private set; } = "";

    public async Task<string> GenerateAsync(
        string studentName, string subjectName,
        IReadOnlyList<DomainPoint> domains, string? subjectNote,
        GradeScale scale, CancellationToken ct = default, string? variationHint = null)
    {
        if (string.IsNullOrWhiteSpace(options.GasUrl))
            throw new InvalidOperationException("생성기 서버(GAS) URL 이 설정되지 않았습니다. ⚙ AI 설정을 확인하세요.");

        // "다르게 다시 생성" 지시는 톤 지시문에 덧붙여 보낸다 (서버는 tonePrompt 를 규칙과 함께 준수)
        var tone = string.Join("\n\n",
            new[] { options.TonePrompt, variationHint }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var (ts, nonce, sig) = GasAuth.Sign("generate");
        var request = new GasRequest(
            "generate", "2", ts, nonce, sig,
            studentName, subjectName,
            domains.Select(d => new GasDomain(d.DomainName, d.Grade, d.CriteriaText, d.Achievement ?? "")).ToList(),
            subjectNote ?? "",
            scale.Levels.ToDictionary(l => l.Label, l => PromptBuilder.ResolveNuance(l.Label, scale)),
            options.TargetCharsFor(subjectName),   // 과목별 지정 우선, 없으면 전역
            tone);

        using var response = await http.PostAsJsonAsync(options.GasUrl, request, Json, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        GasResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<GasResponse>(body, Json);
        }
        catch (JsonException)
        {
            // HTML 오류 페이지 = 옛 배포본 (doPost 미반영) 또는 접근 설정 문제
            throw new InvalidOperationException(
                "생성기 서버가 JSON 이 아닌 응답을 보냈습니다. " +
                "GAS 웹앱이 최신 code.gs(doPost 포함)로 재배포되었고 액세스가 '모든 사용자'인지 확인하세요.");
        }
        if (result is null)
            throw new InvalidOperationException("생성기 서버 응답 파싱 실패");
        if (!result.Ok)
            throw new InvalidOperationException(result.Error ?? "생성기 서버 오류 (상세 없음)");
        LastKeyHint = result.KeyHint ?? "";
        return (result.Text ?? "").Trim();
    }

    private sealed record GasRequest(
        string Action,
        string AuthVersion,
        string Timestamp,
        string Nonce,
        string Signature,
        string StudentName,
        string SubjectName,
        List<GasDomain> Domains,
        string SubjectNote,
        Dictionary<string, string> ScaleNuances,
        int TargetChars,
        string TonePrompt);

    private sealed record GasDomain(
        string DomainName, string Grade, string CriteriaText, string Achievement);

    private sealed record GasResponse(
        bool Ok,
        string? Text,
        string? Error,
        string? KeyHint);
}
