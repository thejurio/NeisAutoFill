using System.IO;
using System.Text.Json;
using NeisAutoFill.Core;

namespace NeisAutoFill.App.Services;

/// <summary>학급 모드 설정 (profiles.json, 공용). 담임=기본, 전담=현재 (학년·반·과목).</summary>
public sealed class ProfileConfig
{
    /// <summary>"homeroom"(담임) / "subject"(전담).</summary>
    public string Mode { get; set; } = "homeroom";

    // 전담의 현재 작업 축 (메인 화면에서 바꾼다). 담임이면 무시.
    public int CurrentGrade { get; set; }
    public string CurrentClass { get; set; } = "";
    public string CurrentSubject { get; set; } = "";
}

/// <summary>
/// 학급 모드 저장·조회. 담임이면 기본 프로필 → 기존 동작과 100% 동일.
/// 전담이면 현재 (학년·반·과목) 조합을 <see cref="AppPaths.CurrentProfile"/> 로 반영한다.
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

    /// <summary>현재 작업 조합 (전담·완성된 축일 때). 없으면 null.</summary>
    public TeachingUnit? CurrentUnit =>
        IsSubjectMode && Config.CurrentGrade > 0 && Config.CurrentClass.Length > 0 && Config.CurrentSubject.Length > 0
            ? new TeachingUnit(Config.CurrentGrade, Config.CurrentClass, Config.CurrentSubject)
            : null;

    /// <summary>실제 적용할 프로필명 — 담임이거나 축이 불완전하면 기본, 전담이면 조합 표시명.</summary>
    public string EffectiveProfile() => CurrentUnit?.Display ?? ProfilePaths.Default;

    /// <summary>모드를 바꾼다 (설정에서). 담임↔전담 전환.</summary>
    public void SetMode(bool subject)
    {
        Config.Mode = subject ? "subject" : "homeroom";
        Save();
    }

    /// <summary>현재 작업 조합을 바꾼다 (메인 화면에서). 전담 전용.</summary>
    public void SetCurrentUnit(TeachingUnit unit)
    {
        Config.CurrentGrade = unit.Grade;
        Config.CurrentClass = unit.Class;
        Config.CurrentSubject = unit.Subject;
        Save();
    }

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
