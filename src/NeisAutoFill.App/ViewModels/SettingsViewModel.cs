using System.IO;
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
        _showQuality = o.ShowNarrativeQuality;
        _scaleLarge = o.UiScale >= 1.25;
        _scaleMedium = o.UiScale >= 1.1 && o.UiScale < 1.25;
        _scaleNormal = o.UiScale < 1.1;
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

    private bool _showQuality;
    /// <summary>서술문 품질 점검(바이트 표시·복붙 의심 경고) 표시 여부.</summary>
    public bool ShowNarrativeQuality { get => _showQuality; set => SetProperty(ref _showQuality, value); }

    // 화면 표시 배율 (보통 1.0 / 크게 1.15 / 더 크게 1.3)
    private bool _scaleNormal, _scaleMedium, _scaleLarge;
    public bool ScaleNormal { get => _scaleNormal; set => SetProperty(ref _scaleNormal, value); }
    public bool ScaleMedium { get => _scaleMedium; set => SetProperty(ref _scaleMedium, value); }
    public bool ScaleLarge { get => _scaleLarge; set => SetProperty(ref _scaleLarge, value); }

    // ── 일반 ──────────────────────────────
    public IReadOnlyList<NeisRegion> Regions => NeisRegions.All;

    private NeisRegion _selectedRegion;
    public NeisRegion SelectedRegion { get => _selectedRegion; set => SetProperty(ref _selectedRegion, value); }

    private bool _speedFast, _speedNormal, _speedSlow;
    public bool SpeedFast { get => _speedFast; set => SetProperty(ref _speedFast, value); }
    public bool SpeedNormal { get => _speedNormal; set => SetProperty(ref _speedNormal, value); }
    public bool SpeedSlow { get => _speedSlow; set => SetProperty(ref _speedSlow, value); }

    /// <summary>버전·업데이트 날짜 (exe 파일 시각 = 마지막 설치/업데이트 시점).</summary>
    public string VersionInfo
    {
        get
        {
            var version = UpdateService.CurrentVersion.ToString(3);
            try
            {
                var exe = Environment.ProcessPath;
                var date = exe is not null ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd") : "";
                return date == "" ? $"버전 v{version}" : $"버전 v{version} · {date} 업데이트";
            }
            catch { return $"버전 v{version}"; }
        }
    }

    /// <summary>검증 후 전체 저장. 실패 시 오류 메시지 반환 (null = 성공).</summary>
    public string? TrySave()
    {
        if (Scale.TrySave() is { } scaleError) return scaleError;

        if (!TryNonNegative(TargetChars, out var chars)) return "생성 글자 수는 0 이상의 숫자여야 합니다 (0 = AI 자율).";
        if (!TryNonNegative(MaxDomains, out var domains)) return "최대 영역 수는 0 이상의 숫자여야 합니다 (0 = 전체).";

        var speed = SpeedSlow ? "slow" : SpeedNormal ? "normal" : "fast";
        var scale = ScaleLarge ? 1.3 : ScaleMedium ? 1.15 : 1.0;
        _settings.Options = _settings.Options with
        {
            TargetChars = chars,
            MaxDomains = domains,
            TonePrompt = TonePrompt.Trim(),
            ShowNarrativeQuality = ShowNarrativeQuality,
            NeisRegionCode = SelectedRegion.Code,
            ClickSpeed = speed,
            UiScale = scale,
        };
        _settings.Save();
        Automation.Timings.SetSpeed(speed);
        Services.UiScaler.Scale = scale;
        Services.UiScaler.ApplyToAll();   // 열린 창에 즉시 반영(크기 완전 반영은 재시작)
        return null;
    }

    private static bool TryNonNegative(string text, out int value) =>
        int.TryParse(text.Trim(), out value) && value >= 0;
}
