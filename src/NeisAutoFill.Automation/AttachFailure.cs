namespace NeisAutoFill.Automation;

/// <summary>
/// 자동 연결(attach) 실패를 사용자 안내 문구로 옮긴다.
/// 실패는 조용히 재시도하되 "다음에 뭘 해야 하는지"는 반드시 보여준다 — 침묵이 v1.3.1~1.6.5 의
/// 연결 불가 버그를 몇 주간 안 보이게 만들었다(단일 exe 게시에서 .playwright 드라이버 누락).
/// </summary>
public static class AttachFailure
{
    /// <summary>브라우저는 붙을 수 있는데 나이스 탭이 없을 때.</summary>
    public const string BrowserOpenNotNeis =
        "나이스 전용 브라우저는 열렸어요. 나이스에 로그인하고 [교과별 평가](또는 [학기말 종합의견])를 조회하면 자동으로 연결됩니다.";

    /// <summary>전용 브라우저가 아직 안 떠 있을 때 (가장 흔한 정상 상태).</summary>
    public const string BrowserNotLaunched =
        "아직 연결되지 않았어요. [🌐 NEIS 접속] 버튼으로 전용 브라우저를 여세요. (평소 쓰던 Edge 를 그냥 열면 연결되지 않습니다)";

    /// <summary>프로그램 파일이 온전하지 않을 때 — exe 만 따로 옮기면 반드시 이 상태가 된다.</summary>
    public const string DriverMissing =
        "프로그램 파일이 온전하지 않아 나이스에 연결할 수 없어요. 받은 zip 을 폴더째 풀고 그 안의 exe 를 실행해 주세요 " +
        "(exe 파일 하나만 옮기면 옆의 .playwright 폴더가 없어 연결되지 않습니다).";

    /// <summary>attach 예외 → 안내 문구. 원인을 못 가리면 가장 흔한 경우로 안내한다.</summary>
    public static string Describe(Exception ex)
    {
        var msg = ex.Message ?? "";

        // Playwright 드라이버(node.exe) 부재 — 배포본이 깨진 경우. 사용자가 스스로 풀 수 있는 유일한 방법이 재설치다.
        if (msg.Contains("Driver not found") || msg.Contains(".playwright"))
            return DriverMissing;

        // 우리가 던진 메시지 — 브라우저는 살아 있는데 나이스 탭이 없다
        if (ex is InvalidOperationException && (msg.Contains("neis") || msg.Contains("탭")))
            return BrowserOpenNotNeis;

        return BrowserNotLaunched;
    }
}
