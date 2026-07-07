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

    private readonly string _path;
    private readonly Dictionary<(string Subject, string No, string Name), string> _map = new();

    public NarrativeStore(string path)
    {
        _path = path;
        Load();
    }

    public string? Get(string subject, string no, string name) =>
        _map.TryGetValue((subject, no, name), out var t) ? t : null;

    public void Set(string subject, string no, string name, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) _map.Remove((subject, no, name));
        else _map[(subject, no, name)] = text;
        Save();
    }

    public void Remove(string subject, string no, string name)
    {
        if (_map.Remove((subject, no, name))) Save();
    }

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
