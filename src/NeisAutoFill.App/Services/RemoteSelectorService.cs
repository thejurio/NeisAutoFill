using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NeisAutoFill.Automation;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 앱 시작 시 GAS 에서 원격 셀렉터 구성(F14)을 받아 <see cref="NeisSelectors"/> 에 적용한다.
/// 나이스 UI 개편 시 GAS 만 갱신하면 앱 재배포 없이 대응된다.
/// 실패·오프라인·빈 응답이면 내장 기본값을 그대로 쓴다(현재 동작) — 절대 앱을 막지 않는다.
/// </summary>
public sealed class RemoteSelectorService(GeneratorSettingsStore settings)
{
    private static HttpClient Http => AppHttp.Short;

    public async Task ApplyAsync()
    {
        var url = settings.Options.GasUrl?.Trim();
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var (ts, nonce, sig) = GasAuth.Sign("selectors");
            var payload = new
            {
                action = "selectors",
                authVersion = "2", timestamp = ts, nonce, signature = sig,
                clientName = $"NeisAutoFill ({Environment.UserName})",
            };
            using var resp = await Http.PostAsJsonAsync(url, payload);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return;
            if (!root.TryGetProperty("selectors", out var sel) || sel.ValueKind != JsonValueKind.Object) return;

            var overrides = new Dictionary<string, string>();
            foreach (var p in sel.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    overrides[p.Name] = p.Value.GetString() ?? "";

            if (overrides.Count == 0) return;   // 원격이 비어 있음 → 기본값 유지
            NeisSelectors.ApplyRemote(overrides);   // 검증 통과분만 적용, 나머지는 기본값 유지
        }
        catch (Exception ex)
        {
            Diag.Swallow(ex, "원격 셀렉터 적용");   // 실패는 조용히 — 기본값으로 계속 동작
        }
    }
}
