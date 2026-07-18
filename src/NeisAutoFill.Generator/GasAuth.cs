using System.Security.Cryptography;
using System.Text;

namespace NeisAutoFill.Generator;

/// <summary>
/// GAS 요청 서명 (V2 HMAC). 모든 doPost 페이로드에 authVersion/timestamp/nonce/signature 를 넣어
/// 서버(code.gs)가 공유 시크릿을 아는 클라이언트인지 + 리플레이가 아닌지 검증하게 한다.
///
/// 시크릿 자체는 <c>GasSecret.cs</c>(gitignore, 공개 저장소 제외)에서 partial 로 주입한다.
/// 그 파일이 없으면 Secret 은 빈 문자열이 되어 서명이 서버와 불일치 → 외부 클론은 인증 실패(의도된 동작).
/// </summary>
public static partial class GasAuth
{
    /// <summary>gitignore 된 GasSecret.cs 가 구현. 없으면 호출이 제거되어 secret 은 "" 로 남는다.</summary>
    static partial void LoadSecret(ref string secret);

    private static string Secret
    {
        get { string s = ""; LoadSecret(ref s); return s; }
    }

    /// <summary>주어진 action 에 대한 (timestamp, nonce, signature) 를 만든다.</summary>
    public static (string Timestamp, string Nonce, string Signature) Sign(string action)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var canonical = $"{action}|{ts}|{nonce}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return (ts, nonce, sig);
    }
}
