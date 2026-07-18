using System.IO;
using System.Text.Json;

namespace NeisAutoFill.Core;

/// <summary>
/// 생성된 서술문 영속화 — narratives.json. 키 = (과목, 번호, 이름).
/// 생성/수정 때마다 저장하고, 앱 재실행 시 생성기 화면에 자동 복원된다.
/// </summary>
public sealed class NarrativeStore
{
    private sealed record Entry(string Subject, string No, string Name, string Text);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private string _path;
    private readonly Dictionary<(string Subject, string No, string Name), string> _map = new();

    public NarrativeStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>저장 경로를 바꾸고 그 파일 내용으로 재로드 (전담 조합 인플레이스 전환용, F9 M5).
    /// 같은 경로면 아무 것도 안 함.</summary>
    public void SwitchTo(string path)
    {
        if (_path == path) return;
        _path = path;
        _map.Clear();
        Load();
        Changed?.Invoke();
    }

    /// <summary>내용 변경 시 발생 (엑셀 미러 등 후처리용). store 는 UI 스레드에서만 접근한다.</summary>
    public event Action? Changed;

    public string? Get(string subject, string no, string name) =>
        _map.TryGetValue((subject, no, name), out var t) ? t : null;

    /// <summary>전체 항목 (과목·번호·이름·서술문). 엑셀 미러·일괄 내보내기용.</summary>
    public IReadOnlyList<(string Subject, string No, string Name, string Text)> All() =>
        _map.Select(kv => (kv.Key.Subject, kv.Key.No, kv.Key.Name, kv.Value)).ToList();

    public void Set(string subject, string no, string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) _map.Remove((subject, no, name));
        else _map[(subject, no, name)] = text;
        Save();
        Changed?.Invoke();
    }

    public void Remove(string subject, string no, string name)
    {
        if (_map.Remove((subject, no, name))) { Save(); Changed?.Invoke(); }
    }

    // NOTE: 저장은 의도적으로 동기·즉시 — 생성 완료분이 어떤 시점에 꺼져도 보존된다는 보장이
    // 배치 중 쓰기 횟수 절약보다 중요 (디바운스 검토 후 기각, 2026-07-17 리팩토링계획 B4).
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var list = _map.Select(kv => new Entry(kv.Key.Subject, kv.Key.No, kv.Key.Name, kv.Value)).ToList();
            File.WriteAllText(_path, JsonSerializer.Serialize(list, Json));
        }
        catch (IOException) { /* 저장 실패는 치명적이지 않음 — 다음 저장에서 재시도 */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path), Json);
            if (list is null) return;
            foreach (var e in list)
                _map[(e.Subject, e.No, e.Name)] = e.Text;
        }
        catch (JsonException) { /* 손상 파일은 무시 (새로 생성됨) */ }
    }
}
