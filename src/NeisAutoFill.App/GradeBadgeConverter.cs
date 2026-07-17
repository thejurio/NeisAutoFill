using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace NeisAutoFill.App;

/// <summary>
/// 등급 라벨 → 배지 색. 척도 순서(상위=초록 … 하위=빨강)를 이름 기반 휴리스틱으로 매핑.
/// 알려진 라벨은 고정색, 그 외에는 뉴트럴 회색.
/// </summary>
public sealed class GradeBadgeConverter : IValueConverter
{
    public enum Part { Background, Foreground }
    public Part Target { get; set; } = Part.Background;

    // (배경, 글자)
    private static readonly (Color bg, Color fg) Green = (Rgb(0xDC, 0xFC, 0xE7), Rgb(0x15, 0x80, 0x3D));
    private static readonly (Color bg, Color fg) Yellow = (Rgb(0xFE, 0xF9, 0xC3), Rgb(0xA1, 0x62, 0x07));
    private static readonly (Color bg, Color fg) Red = (Rgb(0xFE, 0xE2, 0xE2), Rgb(0xB9, 0x1C, 0x1C));
    private static readonly (Color bg, Color fg) Neutral = (Rgb(0xF1, 0xF5, 0xF9), Rgb(0x47, 0x55, 0x69));
    // 미입력 — 옅은 주황으로 눈에 띄게 (입력 누락 발견용)
    private static readonly (Color bg, Color fg) Empty = (Rgb(0xFF, 0xF3, 0xE0), Rgb(0xC2, 0x71, 0x0C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value?.ToString() ?? "").Trim();
        var pair = Classify(s);
        var c = Target == Part.Background ? pair.bg : pair.fg;
        return new SolidColorBrush(c);
    }

    private static (Color bg, Color fg) Classify(string label)
    {
        if (label.Length == 0) return Empty;

        // 상위 계열
        if (label.Contains("잘함") || label == "상" || label.Contains("우수") || label.Contains("매우"))
            return Green;
        // 하위 계열
        if (label.Contains("노력") || label == "하" || label.Contains("미흡") || label.Contains("부족"))
            return Red;
        // 중간 계열
        if (label.Contains("보통") || label == "중" || label.Contains("양호"))
            return Yellow;

        return Neutral;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
