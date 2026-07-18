using System.IO;
using System.Text.Json;
using NeisAutoFill.Core;

namespace NeisAutoFill.App.Services;

/// <summary>학급 모드 설정 (profiles.json, 공용). 담임=기본, 전담=담당 등록 + 현재 조합.</summary>
public sealed class ProfileConfig
{
    /// <summary>"homeroom"(담임) / "subject"(전담).</summary>
    public string Mode { get; set; } = "homeroom";
    /// <summary>전담 담당 등록 (학년·반·과목). 담임이면 무시.</summary>
    public SubjectAssignment Assignment { get; set; } = new();
    /// <summary>현재 활성 작업 조합의 표시명 (예: "3-1 영어"). 담임이면 무시.</summary>
    public string? CurrentUnit { get; set; }
}

/// <summary>
/// 학급 모드·전담 등록 저장·조회. 담임 모드면 항상 기본 프로필 → 기존 동작과 100% 동일.
/// 전담 모드면 현재 작업 조합을 <see cref="AppPaths.CurrentProfile"/> 로 반영한다(전환은 재시작).
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public ProfileConfig Config { get; private set; }

    public ProfileStore()
    {
        Config = Load();
        AppPaths.CurrentProfile = EffectiveProfile();   // 시작 시 경로 계층에 반영
    }

    public bool IsSubjectMode => Config.Mode == "subject";

    /// <summary>현재 활성 조합 (전담·유효할 때만). 없으면 null.</summary>
    public TeachingUnit? CurrentUnit =>
        IsSubjectMode ? Config.Assignment.FindByDisplay(Config.CurrentUnit) : null;

    /// <summary>실제 적용할 프로필명 — 담임이거나 조합이 유효하지 않으면 기본. 전담이면 조합 표시명.</summary>
    public string EffectiveProfile() => CurrentUnit?.Display ?? ProfilePaths.Default;

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
