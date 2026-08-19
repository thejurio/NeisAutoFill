using System.IO;
using System.Text.Json;
using NeisAutoFill.Core.Timetable;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 연간 입력이 어디까지 끝났는지를 <c>%AppData%\NeisAutoFill\timetable_checkpoints.json</c> 에 남긴다
/// (기술설계 §12, 로드맵 T8).
///
/// 앱이 꺼지거나 나이스 세션이 끊겨도 처음부터 다시 하지 않게 하는 것이 목적이다.
/// <b>기록 자체는 권위가 아니다</b> — 재개할 때 값의 최종 판단은 나이스 현재 값으로 한다
/// (<see cref="NeisAutoFill.Automation.TimetableBatchRunner"/>).
///
/// 담기는 것은 학교·사용자 <b>해시</b>와 주 시작일뿐이라 이 파일에 개인정보가 남지 않는다(D-012).
/// </summary>
public sealed class TimetableCheckpointStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(AppPaths.Root, "timetable_checkpoints.json");

    private readonly List<TimetableRunCheckpoint> _items = Load();

    /// <summary>이 범위에 남은 기록. 없으면 null.</summary>
    public TimetableRunCheckpoint? Find(TimetableProfileScope scope) =>
        _items.FirstOrDefault(c => c.Scope == scope);

    /// <summary>
    /// 이어서 쓸 수 있는 기록을 찾는다.
    /// 원본이나 나이스 목록이 바뀌었으면 <b>이어서 쓰지 않고</b> 새로 시작한다 —
    /// 끝났다고 적힌 주가 지금 계획과 다른 내용일 수 있기 때문이다.
    /// </summary>
    /// <param name="blocker">이어서 못 쓰는 이유 — 사용자에게 그대로 보여 준다. 이어서 쓸 수 있으면 null</param>
    public TimetableRunCheckpoint Resume(
        TimetableProfileScope scope, string planFingerprint, string catalogFingerprint, out string? blocker)
    {
        var found = Find(scope);
        blocker = found?.ResumeBlocker(scope, planFingerprint, catalogFingerprint);

        return found is not null && blocker is null
            ? found
            : TimetableRunCheckpoint.Start(scope, planFingerprint, catalogFingerprint, DateTimeOffset.Now);
    }

    /// <summary>같은 범위의 기록을 덮어쓰고 즉시 파일에 남긴다 (주 하나가 끝날 때마다 불린다).</summary>
    public void Save(TimetableRunCheckpoint checkpoint)
    {
        _items.RemoveAll(c => c.Scope == checkpoint.Scope);
        _items.Add(checkpoint);
        Persist();
    }

    /// <summary>기록을 지운다 — 처음부터 다시 하고 싶을 때.</summary>
    public void Delete(TimetableProfileScope scope)
    {
        _items.RemoveAll(c => c.Scope == scope);
        Persist();
    }

    private static List<TimetableRunCheckpoint> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            return JsonSerializer.Deserialize<List<TimetableRunCheckpoint>>(File.ReadAllText(FilePath), Json)
                   ?? new();
        }
        catch (Exception)
        {
            // 기록을 못 읽는다고 앱이 죽으면 안 된다 — 이 클래스는 DI 생성자에서 만들어진다.
            // 최악의 경우 처음부터 다시 하면 되고, 이미 맞는 칸은 건너뛴다.
            return new();
        }
    }

    private void Persist()
    {
        try
        {
            AppPaths.EnsureRoot();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items, Json));
        }
        catch (IOException) { /* 다음 주가 끝날 때 다시 시도한다 */ }
    }
}
