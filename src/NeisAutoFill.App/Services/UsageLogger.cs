using System.Net.Http;
using System.Net.Http.Json;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 사용 기록을 GAS(RequestLog 시트)에 남긴다 — 실행(startup) 1건 + 생성 배치당 1건.
/// (학생당 개별 기록은 하지 않는다 — 사용자 결정. 실패는 GAS 쪽에서 기록)
/// 기록 실패는 조용히 무시 — 사용에 지장 없음.
/// </summary>
public sealed class UsageLogger(GeneratorSettingsStore settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private string ClientName => $"NeisAutoFill ({Environment.UserName})";

    public Task LogStartupAsync(string version) =>
        PostAsync(new { action = "startup", version, clientName = ClientName });

    /// <summary>생성 배치 시작 기록 (예: "국어 24명 · 수학 20명").</summary>
    public Task LogBatchAsync(string info) =>
        PostAsync(new { action = "logBatch", info, clientName = ClientName });

    private async Task PostAsync(object payload)
    {
        var url = settings.Options.GasUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        try { using var _ = await Http.PostAsJsonAsync(url, payload); }
        catch { /* 기록 실패는 무시 */ }
    }
}
