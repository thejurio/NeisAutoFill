using System.IO;
using System.Text.Json;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.Services;

/// <summary>생성기 설정(백엔드 선택·GAS URL·Gemini 키)을 settings.json 으로 영속화.</summary>
public sealed class GeneratorSettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public GeneratorOptions Options { get; set; }

    public GeneratorSettingsStore()
    {
        Options = Load();
    }

    private static GeneratorOptions Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsJson))
                return JsonSerializer.Deserialize<GeneratorOptions>(
                    File.ReadAllText(AppPaths.SettingsJson), Json) ?? new GeneratorOptions();
        }
        catch (JsonException) { /* 손상 시 기본값 */ }
        return new GeneratorOptions();
    }

    public void Save()
    {
        AppPaths.EnsureRoot();
        File.WriteAllText(AppPaths.SettingsJson, JsonSerializer.Serialize(Options, Json));
    }
}
