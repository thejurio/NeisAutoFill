using System.Net.Http;
using System.Net.Http.Json;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 프로그램 시작을 GAS(RequestLog 시트)에 기록. 서술문 생성 기록은 GAS 서버가
/// doPost 안에서 자동으로 남기므로 여기서는 시작(startup)만 보낸다.
/// 기록 실패는 조용히 무시 — 사용에 지장 없음.
/// </summary>
public sealed class UsageLogger(GeneratorSettingsStore settings)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task LogStartupAsync(string version)
    {
        var url = settings.Options.GasUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var payload = new
            {
                action = "startup",
                version,
                clientName = $"NeisAutoFill ({Environment.UserName})",
            };
            using var _ = await Http.PostAsJsonAsync(url, payload);
        }
        catch { /* 기록 실패는 무시 */ }
    }
}
