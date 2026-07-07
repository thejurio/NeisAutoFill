using System.Text.Json;

namespace NeisAutoFill.Core.Scale;

/// <summary>
/// scales.json 파일 기반 척도 저장소. 내장 프리셋(GradePresets)은 항상 포함되고
/// 삭제 불가. 사용자 정의 척도만 파일에 저장된다.
/// </summary>
public sealed class JsonScaleStore : IScaleStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly List<GradeScale> _userScales = new();
    private string _activeName;

    public JsonScaleStore(string path)
    {
        _path = path;
        _activeName = GradePresets.ThreeLevel.Name;
        Load();
    }

    public IReadOnlyList<GradeScale> Presets =>
        GradePresets.All.Concat(_userScales).ToList();

    public GradeScale Active
    {
        get => Presets.FirstOrDefault(s => s.Name == _activeName) ?? GradePresets.ThreeLevel;
        set => _activeName = value.Name;
    }

    private bool IsBuiltIn(string name) => GradePresets.All.Any(s => s.Name == name);

    public void Upsert(GradeScale scale)
    {
        if (IsBuiltIn(scale.Name))
            throw new InvalidOperationException($"내장 프리셋 '{scale.Name}'은 수정할 수 없습니다. 다른 이름으로 저장하세요.");
        _userScales.RemoveAll(s => s.Name == scale.Name);
        _userScales.Add(scale);
    }

    public bool Remove(string name)
    {
        if (IsBuiltIn(name)) return false;
        return _userScales.RemoveAll(s => s.Name == name) > 0;
    }

    public void Save()
    {
        var dto = new StoreDto(_activeName, _userScales);
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(dto, JsonOptions));
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(_path), JsonOptions);
            if (dto is null) return;
            _userScales.Clear();
            foreach (var s in dto.UserScales.Where(s => !IsBuiltIn(s.Name)))
                _userScales.Add(s);
            if (!string.IsNullOrWhiteSpace(dto.ActiveName))
                _activeName = dto.ActiveName;
        }
        catch (JsonException)
        {
            // 손상된 파일은 무시하고 기본값 사용 (설정 파일이라 예외로 앱을 죽이지 않음)
        }
    }

    private sealed record StoreDto(string ActiveName, List<GradeScale> UserScales);
}
