using NeisAutoFill.App.Mvvm;
using NeisAutoFill.App.Services;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.App.ViewModels;

/// <summary>
/// 통합 설정 창 — AI 서술문(글자 수·영역 수·톤), 평가 단계(척도), 일반(교육청·자동클릭 속도).
/// 단계별 톤은 평가 단계 탭의 'AI 서술 뉘앙스' 컬럼에서 편집한다.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly GeneratorSettingsStore _settings;

    public SettingsViewModel(IScaleStore scales, GeneratorSettingsStore settings)
    {
        _settings = settings;
        Scale = new ScaleEditorViewModel(scales);

        var o = settings.Options;
        _targetChars = o.TargetChars.ToString();
        _maxDomains = o.MaxDomains.ToString();
        _tonePrompt = o.TonePrompt;
        _selectedRegion = NeisRegions.Find(o.NeisRegionCode);
        _speedFast = o.ClickSpeed is not ("normal" or "slow");
        _speedNormal = o.ClickSpeed == "normal";
        _speedSlow = o.ClickSpeed == "slow";
    }

    /// <summary>평가 단계 탭 (기존 척도 편집기 재사용 — 단계 수·이름·AI 뉘앙스).</summary>
    public ScaleEditorViewModel Scale { get; }

    // ── AI 서술문 ──────────────────────────
    private string _targetChars;
    public string TargetChars { get => _targetChars; set => SetProperty(ref _targetChars, value); }

    private string _maxDomains;
    public string MaxDomains { get => _maxDomains; set => SetProperty(ref _maxDomains, value); }

    private string _tonePrompt;
    public string TonePrompt { get => _tonePrompt; set => SetProperty(ref _tonePrompt, value); }

    // ── 일반 ──────────────────────────────
    public IReadOnlyList<NeisRegion> Regions => NeisRegions.All;

    private NeisRegion _selectedRegion;
    public NeisRegion SelectedRegion { get => _selectedRegion; set => SetProperty(ref _selectedRegion, value); }

    private bool _speedFast, _speedNormal, _speedSlow;
    public bool SpeedFast { get => _speedFast; set => SetProperty(ref _speedFast, value); }
    public bool SpeedNormal { get => _speedNormal; set => SetProperty(ref _speedNormal, value); }
    public bool SpeedSlow { get => _speedSlow; set => SetProperty(ref _speedSlow, value); }

    /// <summary>검증 후 전체 저장. 실패 시 오류 메시지 반환 (null = 성공).</summary>
    public string? TrySave()
    {
        if (Scale.TrySave() is { } scaleError) return scaleError;

        if (!TryNonNegative(TargetChars, out var chars)) return "생성 글자 수는 0 이상의 숫자여야 합니다 (0 = AI 자율).";
        if (!TryNonNegative(MaxDomains, out var domains)) return "최대 영역 수는 0 이상의 숫자여야 합니다 (0 = 전체).";

        var speed = SpeedSlow ? "slow" : SpeedNormal ? "normal" : "fast";
        _settings.Options = _settings.Options with
        {
            TargetChars = chars,
            MaxDomains = domains,
            TonePrompt = TonePrompt.Trim(),
            NeisRegionCode = SelectedRegion.Code,
            ClickSpeed = speed,
        };
        _settings.Save();
        Automation.Timings.SetSpeed(speed);
        return null;
    }

    private static bool TryNonNegative(string text, out int value) =>
        int.TryParse(text.Trim(), out value) && value >= 0;
}
