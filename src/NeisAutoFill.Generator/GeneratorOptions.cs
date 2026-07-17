namespace NeisAutoFill.Generator;

/// <summary>
/// 생성기 설정. settings.json 으로 영속화.
/// 생성은 GAS 웹앱을 통해서만 — API 키는 GAS 쪽 APIKeys 시트에서 관리되므로 여기 없다.
/// </summary>
public sealed record GeneratorOptions
{
    /// <summary>GAS 웹앱 /exec URL (주소.txt 의 배포 주소가 기본값).</summary>
    public string GasUrl { get; init; } =
        "https://script.google.com/macros/s/AKfycbwI5UD-u2603Y2clUlmGDYR98N93yEsdmoMuOj_GM5hVpz4ei1NSNXXXhJ688hwl8d8sA/exec";

    /// <summary>서술문 나이스 입력 시 UTF-8 바이트 제한 사전검사 (0 = 검사 안 함).</summary>
    public int MaxNarrativeBytes { get; init; } = 0;

    /// <summary>생성문 목표 글자 수 (0 = AI 자율).</summary>
    public int TargetChars { get; init; } = 0;

    /// <summary>서술문에 반영할 최대 영역 수 (0 = 전체).</summary>
    public int MaxDomains { get; init; } = 0;

    /// <summary>전체 서술 톤 지시문 (비우면 기본 톤). 단계별 톤은 평가척도의 AI 뉘앙스에서.</summary>
    public string TonePrompt { get; init; } = "";

    /// <summary>자동클릭 속도: fast(현재 기본)/normal/slow — 느린 PC 안정용.</summary>
    public string ClickSpeed { get; init; } = "fast";

    /// <summary>나이스 접속 지역 (교육청 코드, 기본 전북).</summary>
    public string NeisRegionCode { get; init; } = "jbe";

    /// <summary>자동업데이트용 GitHub 저장소 ("owner/repo"). 비우면 업데이트 확인 안 함.</summary>
    public string UpdateRepo { get; init; } = "thejurio/NeisAutoFill";

    /// <summary>❓ 도움말 버튼이 여는 사용법 페이지 URL. 비우면 "준비 중" 안내.</summary>
    public string HelpUrl { get; init; } = "";
}
