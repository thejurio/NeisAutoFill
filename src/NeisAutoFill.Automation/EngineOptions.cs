namespace NeisAutoFill.Automation;

/// <summary>엔진 실행 옵션 (Edge 경로·포트·URL). 앱 설정에서 주입.</summary>
public sealed record EngineOptions
{
    public string DebugAddress { get; init; } = "127.0.0.1:9222";
    public int DebugPort { get; init; } = 9222;
    public string ProfileDir { get; init; } = @"C:\neis_automation\edge_profile";

    /// <summary>나이스 접속 주소 — 지역 선택 시 갱신되므로 set 가능.</summary>
    public string NeisUrl { get; set; } = "https://jbe.neis.go.kr";

    public IReadOnlyList<string> EdgePaths { get; init; } = new[]
    {
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    };
}
