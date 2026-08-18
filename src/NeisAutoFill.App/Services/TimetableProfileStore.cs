using System.IO;
using System.Text.Json;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 시간표 매핑 프로필을 <c>%AppData%\NeisAutoFill\timetable_mappings.json</c> 에 영속화 (기술설계 §13).
///
/// 범위(교육청·학교해시·사용자해시·학년도·학기·학년·반)별로 한 건씩 보관한다.
/// 학교·사용자는 <b>해시로만</b> 저장되므로 이 파일에 개인정보가 남지 않는다(D-012).
/// </summary>
public sealed class TimetableProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Path_ => System.IO.Path.Combine(AppPaths.Root, "timetable_mappings.json");

    private List<TimetableMappingProfile> _profiles = Load();

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
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<List<TimetableMappingProfile>>(
                    File.ReadAllText(Path_), Json) ?? new();
        }
        catch (JsonException) { /* 손상 시 빈 목록 — 매핑은 다시 만들면 된다 */ }
        catch (IOException) { }
        return new();
    }

    private void Persist()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(Path_, JsonSerializer.Serialize(_profiles, Json));
    }
}
