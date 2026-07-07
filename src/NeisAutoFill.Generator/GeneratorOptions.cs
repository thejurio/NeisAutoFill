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

    /// <summary>나이스 접속 지역 (교육청 코드, 기본 전북).</summary>
    public string NeisRegionCode { get; init; } = "jbe";

    /// <summary>자동업데이트용 GitHub 저장소 ("owner/repo"). 비우면 업데이트 확인 안 함.</summary>
    public string UpdateRepo { get; init; } = "";
}
