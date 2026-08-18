using NeisAutoFill.Automation;

namespace NeisAutoFill.Tests;

/// <summary>
/// 자동 연결 실패 안내 분류. 배포본에 Playwright 드라이버가 빠지면(단일 exe 게시 사고)
/// 사용자는 "연결이 안 된다"만 겪고 원인을 알 길이 없었다 — 그 침묵을 막는 테스트.
/// </summary>
public class AttachFailureTests
{
    [Fact]
    public void 드라이버_누락은_재설치_안내를_준다()
    {
        var ex = new InvalidOperationException(
            @"Driver not found: C:\Users\user\.playwright\node\win32_x64\node.exe");

        Assert.Equal(AttachFailure.DriverMissing, AttachFailure.Describe(ex));
    }

    [Fact]
    public void 나이스_탭이_없으면_로그인_조회를_안내한다()
    {
        var ex = new InvalidOperationException("neis.go.kr 탭을 찾지 못했습니다. NEIS에 접속해 주세요.");

        Assert.Equal(AttachFailure.BrowserOpenNotNeis, AttachFailure.Describe(ex));
    }

    [Fact]
    public void 브라우저가_안_떠_있으면_접속_버튼을_안내한다()
    {
        var ex = new InvalidOperationException("connect ECONNREFUSED 127.0.0.1:9222");

        Assert.Equal(AttachFailure.BrowserNotLaunched, AttachFailure.Describe(ex));
    }

    [Fact]
    public void 원인을_모르면_가장_흔한_경우로_안내한다()
    {
        Assert.Equal(AttachFailure.BrowserNotLaunched, AttachFailure.Describe(new Exception("")));
    }
}
