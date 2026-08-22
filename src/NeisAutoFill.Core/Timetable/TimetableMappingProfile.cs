using System.Security.Cryptography;
using System.Text;

namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 매핑 규칙이 유효한 범위 (기술설계 §13).
/// 학교·사용자 식별값은 <b>해시로만</b> 보관한다 — 원문을 남기지 않는다(D-012).
/// </summary>
/// <param name="Host">교육청 호스트 (jbe.neis.go.kr 등 — 개인정보 아님)</param>
/// <param name="SchoolHash">학교 식별값 해시</param>
/// <param name="UserHash">로그인 사용자 식별값 해시</param>
public sealed record TimetableProfileScope(
    string Host,
    string SchoolHash,
    string UserHash,
    int SchoolYear,
    int Semester,
    int Grade,
    string ClassName)
{
    /// <summary>원문 식별값을 받아 해시로 범위를 만든다. 원문은 저장하지 않는다.</summary>
    public static TimetableProfileScope Create(
        string host, string school, string user,
        int schoolYear, int semester, int grade, string className) =>
        new(host, Hash(school), Hash(user), schoolYear, semester, grade, className);

    /// <summary>짧은 해시 — 로그에 남겨도 원문을 되돌릴 수 없다.</summary>
    public static string Hash(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    public string Describe() => $"{SchoolYear}학년도 {Semester}학기 {Grade}학년 {ClassName}반";
}

/// <summary>
/// 저장된 매핑 프로필 (기술설계 §13).
/// 다른 범위나 카탈로그가 바뀐 뒤에는 <b>자동 확정하지 않는다</b> — 추천 자료로만 쓴다.
/// </summary>
/// <param name="Scope">이 규칙들이 유효한 범위</param>
/// <param name="CatalogFingerprint">규칙을 만들 때의 카탈로그 지문</param>
/// <param name="Rules">매핑 규칙들</param>
/// <param name="SavedAt">저장 시각</param>
public sealed record TimetableMappingProfile(
    TimetableProfileScope Scope,
    string CatalogFingerprint,
    IReadOnlyList<TimetableMappingRule> Rules,
    DateTimeOffset SavedAt)
{
    /// <summary>
    /// 이 프로필을 그대로 재사용해도 되는가.
    /// 범위와 카탈로그 지문이 <b>모두</b> 같아야 한다 — 교사 한 명이 목록에서 빠져도 지문이 달라진다.
    /// </summary>
    public bool CanReuseFor(TimetableProfileScope scope, string catalogFingerprint) =>
        Scope == scope && CatalogFingerprint == catalogFingerprint;

    /// <summary>
    /// 지문이 달라졌을 때 살릴 수 있는 규칙만 남긴다.
    /// 대상이 현재 카탈로그에 <b>그대로 있는</b> 규칙만 통과시킨다.
    ///
    /// <b>사용자 확정 표시는 그대로 둔다.</b> 대상이 하나도 안 변했다면 그때 한 확정은 지금도 유효하다 —
    /// 목록 어딘가에서 남의 교사 한 명이 늘었다는 이유로 다시 묻는 것은 <b>쓸데없이 붙잡는 일</b>이다
    /// (사용자 요청 2026-08-22). 대상이 사라진 규칙은 위에서 이미 걸러졌고,
    /// 그 과목은 담당 교사가 비어 실행이 막히므로 <b>정말 손대야 할 때만</b> 사용자가 끌려온다.
    /// </summary>
    public IReadOnlyList<TimetableMappingRule> RulesStillValid(TimetableCatalog catalog) =>
        Rules
            .Where(r => r.IsSkip || catalog.Find(r.TargetStableKey) is not null)
            .Select(r => r with { CatalogFingerprint = catalog.Fingerprint })
            .ToList();

    /// <summary>목록이 바뀌어 <b>버려진</b> 규칙 수 — 사용자가 다시 골라야 하는 것만 센다.</summary>
    public int LostCount(TimetableCatalog catalog) => Rules.Count - RulesStillValid(catalog).Count;

    /// <summary>지문이 달라져 재확인이 필요한지.</summary>
    public bool NeedsRecheck(string catalogFingerprint) => CatalogFingerprint != catalogFingerprint;
}
