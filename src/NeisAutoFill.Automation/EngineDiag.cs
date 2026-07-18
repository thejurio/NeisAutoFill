namespace NeisAutoFill.Automation;

/// <summary>
/// 엔진에서 조용히 삼키는 예외의 진단 훅. Automation 은 App(Diag)을 참조하지 않으므로,
/// App 이 시작 시 <see cref="OnSwallow"/> 에 Diag.Swallow 를 연결한다. 연결 안 되면 아무 일도 안 함.
/// 안정화 전 "원인 불명" 문제를 추적하기 위한 것 — 동작에는 영향 없다.
/// </summary>
public static class EngineDiag
{
    /// <summary>(예외, 맥락) 을 받는 기록 훅. App 이 Diag.Swallow 로 연결.</summary>
    public static Action<Exception, string>? OnSwallow;

    public static void Swallow(Exception ex, string context)
    {
        try { OnSwallow?.Invoke(ex, context); } catch { /* 진단 실패는 무시 */ }
    }
}
