using System.Net.Http;

namespace NeisAutoFill.App.Services;

/// <summary>앱 공용 HttpClient (용도별 타임아웃). 소켓 고갈 방지 — 새 인스턴스 생성 금지.</summary>
public static class AppHttp
{
    /// <summary>GAS 생성·가져오기 등 오래 걸리는 호출용 (5분).</summary>
    public static readonly HttpClient Long = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>사용 기록 등 실패해도 무방한 짧은 호출용 (10초).</summary>
    public static readonly HttpClient Short = new() { Timeout = TimeSpan.FromSeconds(10) };
}
