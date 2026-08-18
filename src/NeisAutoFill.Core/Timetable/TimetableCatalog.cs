using System.Security.Cryptography;
using System.Text;

namespace NeisAutoFill.Core.Timetable;

/// <summary>
/// 특정 시점에 나이스 메뉴에서 읽어낸 항목 전체 (기술설계 §5 TimetableCatalogSnapshot).
/// <b>이 목록이 최종 권위</b>다 — 과목·교사는 학교·계정마다 다르므로 하드코딩하지 않는다(D-001).
/// </summary>
public sealed class TimetableCatalog
{
    public TimetableCatalog(IEnumerable<NeisTimetableOption> options)
    {
        All = options.ToList();
        Assignable = All.Where(o => o.IsAssignable).ToList();
        Commands = All.Where(o => o.Kind is TimetableOptionKind.Command or TimetableOptionKind.Cancel).ToList();
        Unknown = All.Where(o => o.Kind == TimetableOptionKind.Unknown).ToList();
        _byKey = Assignable
            .GroupBy(o => o.StableKey)
            .ToDictionary(g => g.Key, g => g.First());
    }

    private readonly Dictionary<string, NeisTimetableOption> _byKey;

    /// <summary>메뉴에서 읽은 전부 (명령·미해석 포함).</summary>
    public IReadOnlyList<NeisTimetableOption> All { get; }

    /// <summary>자동 입력 후보가 될 수 있는 항목만.</summary>
    public IReadOnlyList<NeisTimetableOption> Assignable { get; }

    /// <summary>작업 명령·취소 — 후보에서 제외되지만 진단용으로 남긴다.</summary>
    public IReadOnlyList<NeisTimetableOption> Commands { get; }

    /// <summary>구조를 해석하지 못한 항목 — 사용자에게 보고해야 한다(조용히 버리지 않는다).</summary>
    public IReadOnlyList<NeisTimetableOption> Unknown { get; }

    /// <summary>안정 키로 항목 찾기. 없으면 null — 저장된 규칙의 대상이 사라진 경우다.</summary>
    public NeisTimetableOption? Find(string stableKey) =>
        _byKey.TryGetValue(stableKey, out var o) ? o : null;

    /// <summary>
    /// 계정을 뺀 키로 찾기 — 저장 전 셀에는 계정이 없다(R-006).
    /// <b>후보가 둘 이상이면 null</b>. 동명이인을 임의로 고르지 않는다(D-006).
    /// </summary>
    public NeisTimetableOption? FindLoose(string looseKey)
    {
        var hits = Assignable.Where(o => o.LooseKey == looseKey).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>같은 과목의 후보들 (교사가 여러 명이면 여럿). 자동 확정 판단에 쓴다.</summary>
    public IReadOnlyList<NeisTimetableOption> FindBySubject(string subject)
    {
        var norm = TimetableTextNormalizer.Normalize(subject);
        return Assignable.Where(o => TimetableTextNormalizer.Normalize(o.Subject) == norm).ToList();
    }

    /// <summary>
    /// 정렬에 독립적인 카탈로그 지문 (기술설계 §5).
    /// 메뉴 순서나 uuid 가 바뀌어도 구성이 같으면 같은 값이 나온다 — 저장된 매핑 재사용의 판단 기준.
    /// 항목이 하나라도 추가·삭제되면 달라지므로, 달라지면 사용자에게 재확인을 받는다.
    /// </summary>
    public string Fingerprint => _fingerprint ??= ComputeFingerprint(Assignable);
    private string? _fingerprint;

    private static string ComputeFingerprint(IEnumerable<NeisTimetableOption> options)
    {
        var joined = string.Join("\n", options.Select(o => o.StableKey).OrderBy(k => k, StringComparer.Ordinal));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();   // 16자면 충돌 걱정 없이 로그에 남길 만하다
    }
}
