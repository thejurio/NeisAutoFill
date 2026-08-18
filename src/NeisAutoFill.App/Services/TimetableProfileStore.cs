using System.IO;
using System.Text.Json;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 시간표 매핑 프로필을 <c>%AppData%\NeisAutoFill\timetable_mappings.json</c> 에 영속화 (기술설계 §13).
///
/// 범위(교육청·학교해시·사용자해시·학년도·학기·학년·반)별로 한 건씩 보관한다.
/// 학교·사용자는 <b>해시로만</b> 저장되므로 이 파일에 개인정보가 남지 않는다(D-012).
///
/// 저장 스키마는 도메인 모델과 <b>분리</b>한다(아래 Dto). <see cref="MappingScope"/> 는
/// 잘못된 조합을 막으려고 생성자를 감춰 두었기 때문에 그대로는 역직렬화할 수 없다 —
/// 이걸 모르고 그냥 저장했다가 불러오기에서 앱이 죽는 문제를 실제로 겪었다.
/// </summary>
public sealed class TimetableProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppPaths.Root, "timetable_mappings.json");

    private readonly List<TimetableMappingProfile> _profiles = Load();

    /// <summary>이 범위에 저장된 프로필. 없으면 null.</summary>
    public TimetableMappingProfile? Find(TimetableProfileScope scope) =>
        _profiles.FirstOrDefault(p => p.Scope == scope);

    /// <summary>같은 범위의 프로필을 덮어쓰고 저장한다.</summary>
    public void Save(TimetableMappingProfile profile)
    {
        _profiles.RemoveAll(p => p.Scope == profile.Scope);
        _profiles.Add(profile);
        Persist();
    }

    /// <summary>
    /// 다른 범위(다른 반 등)의 프로필들 — <b>추천 자료로만</b> 보여 준다.
    /// 자동 확정에 쓰면 안 된다(기술설계 §13).
    /// </summary>
    public IReadOnlyList<TimetableMappingProfile> Others(TimetableProfileScope scope) =>
        _profiles.Where(p => p.Scope != scope).ToList();

    public void Delete(TimetableProfileScope scope)
    {
        _profiles.RemoveAll(p => p.Scope == scope);
        Persist();
    }

    private static List<TimetableMappingProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var dto = JsonSerializer.Deserialize<List<ProfileDto>>(File.ReadAllText(FilePath), Json);
            return dto?.Select(d => d.ToDomain()).ToList() ?? new();
        }
        catch (Exception)
        {
            // 어떤 이유로든(손상·스키마 변경·권한) 매핑을 못 읽는다고 앱이 죽으면 안 된다.
            // 이 클래스는 DI 생성자에서 만들어지므로 여기서 던지면 프로그램이 시작조차 못 한다.
            return new();
        }
    }

    private void Persist()
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(_profiles.Select(ProfileDto.From).ToList(), Json));
        }
        catch (IOException) { /* 저장 실패는 다음 기회에 — 매핑은 화면에서 다시 만들 수 있다 */ }
    }

    // ── 저장 전용 스키마 ────────────────────────────────────────
    // 도메인 모델이 바뀌어도 파일 포맷은 여기서만 맞추면 된다.

    private sealed record ScopeDto(MappingScopeKind Kind, DayOfWeek? Day, int? Period, DateOnly? Date)
    {
        public static ScopeDto From(MappingScope s) => new(s.Kind, s.Day, s.Period, s.Date);

        /// <summary>팩터리로만 복원한다 — 잘못된 조합이 파일에서 들어와도 기본값으로 떨어진다.</summary>
        public MappingScope ToDomain() => Kind switch
        {
            MappingScopeKind.DayOfWeek when Day is { } d => MappingScope.ForDay(d),
            MappingScopeKind.DayAndPeriod when Day is { } d && Period is { } p => MappingScope.ForDayPeriod(d, p),
            MappingScopeKind.SpecificDate when Date is { } dt && Period is { } p => MappingScope.ForDate(dt, p),
            _ => MappingScope.Default,
        };
    }

    private sealed record RuleDto(string SourceToken, string TargetStableKey, ScopeDto Scope,
        bool IsUserConfirmed, string CatalogFingerprint)
    {
        public static RuleDto From(TimetableMappingRule r) =>
            new(r.SourceToken, r.TargetStableKey, ScopeDto.From(r.Scope), r.IsUserConfirmed, r.CatalogFingerprint);

        public TimetableMappingRule ToDomain() =>
            new(SourceToken, TargetStableKey, Scope.ToDomain(), IsUserConfirmed, CatalogFingerprint);
    }

    private sealed record ProfileDto(TimetableProfileScope Scope, string CatalogFingerprint,
        List<RuleDto> Rules, DateTimeOffset SavedAt)
    {
        public static ProfileDto From(TimetableMappingProfile p) =>
            new(p.Scope, p.CatalogFingerprint, p.Rules.Select(RuleDto.From).ToList(), p.SavedAt);

        public TimetableMappingProfile ToDomain() =>
            new(Scope, CatalogFingerprint, Rules.Select(r => r.ToDomain()).ToList(), SavedAt);
    }
}
