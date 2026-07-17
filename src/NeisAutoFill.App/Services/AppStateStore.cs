using System.IO;
using System.Text.Json;

namespace NeisAutoFill.App.Services;

/// <summary>앱 상태 (마지막·최근 파일 경로). state.json 으로 영속화.</summary>
public sealed class AppState
{
    public string? LastGradePath { get; set; }
    public string? LastPlanPath { get; set; }
    public List<string> RecentGradeFiles { get; set; } = new();
    public List<string> RecentPlanFiles { get; set; } = new();
    public bool ShowCriteriaPanel { get; set; }
    public bool LogExpanded { get; set; }
}

/// <summary>
/// 최근 사용 자료 기록. 시작 시 마지막 자료 자동 로드와 [최근 파일] 메뉴의 근거.
/// 존재하지 않는 파일은 조회 시점에 걸러진다 (기록은 남겨 두면 복구 시 다시 보임).
/// </summary>
public sealed class AppStateStore
{
    private const int MaxRecent = 10;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AppState State { get; }

    public AppStateStore()
    {
        State = Load();
    }

    private static AppState Load()
    {
        try
        {
            if (File.Exists(AppPaths.StateJson))
                return JsonSerializer.Deserialize<AppState>(
                    File.ReadAllText(AppPaths.StateJson), Json) ?? new AppState();
        }
        catch (JsonException) { /* 손상 시 초기화 */ }
        return new AppState();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.StateJson, JsonSerializer.Serialize(State, Json));
    }

    public void TouchGrade(string path)
    {
        State.LastGradePath = path;
        Push(State.RecentGradeFiles, path);
        Save();
    }

    public void TouchPlan(string path)
    {
        State.LastPlanPath = path;
        Push(State.RecentPlanFiles, path);
        Save();
    }

    /// <summary>실존하는 최근 파일만 (최신순).</summary>
    public IReadOnlyList<string> ExistingRecentGrades() => Existing(State.RecentGradeFiles);
    public IReadOnlyList<string> ExistingRecentPlans() => Existing(State.RecentPlanFiles);

    private static void Push(List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > MaxRecent) list.RemoveRange(MaxRecent, list.Count - MaxRecent);
    }

    private static IReadOnlyList<string> Existing(List<string> list) =>
        list.Where(File.Exists).ToList();
}
