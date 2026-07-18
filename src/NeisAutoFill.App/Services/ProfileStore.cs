using System.IO;
using System.Text.Json;
using NeisAutoFill.Core;

namespace NeisAutoFill.App.Services;

/// <summary>다중 학급 프로필 설정 (profiles.json, 공용). 담임=세트 1개, 전담=세트 여러 개.</summary>
public sealed class ProfileConfig
{
    /// <summary>"homeroom"(담임) / "subject"(전담).</summary>
    public string Mode { get; set; } = "homeroom";
    /// <summary>현재 활성 세트(프로필) 이름. 담임이면 무시(기본 사용).</summary>
    public string Current { get; set; } = ProfilePaths.Default;
    /// <summary>전담 모드의 세트 목록 (예: "3-1 영어", "4-2 과학"). 순서 유지.</summary>
    public List<string> Sets { get; set; } = new();
}

/// <summary>
/// 프로필(작업공간 세트) 저장·조회. 담임 모드면 항상 기본 프로필 → 기존 동작과 100% 동일.
/// 전담 모드면 등록된 세트 중 현재 세트를 <see cref="AppPaths.CurrentProfile"/> 로 반영한다(전환은 재시작).
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ProfileConfig Config { get; private set; }

    public ProfileStore()
    {
        Config = Load();
        // 앱 시작 시 현재 프로필을 경로 계층에 반영 (담임=기본, 전담=현재 세트)
        AppPaths.CurrentProfile = EffectiveProfile();
    }

    /// <summary>실제로 적용할 프로필명 — 담임이거나 세트가 유효하지 않으면 기본.</summary>
    public string EffectiveProfile()
    {
        if (!IsSubjectMode) return ProfilePaths.Default;
        return Config.Sets.Contains(Config.Current) ? Config.Current : ProfilePaths.Default;
    }

    public bool IsSubjectMode => Config.Mode == "subject";

    private ProfileConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ProfilesJson))
                return JsonSerializer.Deserialize<ProfileConfig>(File.ReadAllText(AppPaths.ProfilesJson), Json)
                       ?? new ProfileConfig();
        }
        catch { /* 손상 시 기본 */ }
        return new ProfileConfig();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.ProfilesJson, JsonSerializer.Serialize(Config, Json));
    }
}
