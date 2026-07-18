using System.Windows;
using System.Windows.Media;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 화면 표시 배율 적용 — 모든 창 콘텐츠에 LayoutTransform(ScaleTransform)을 걸어 글꼴·요소를 함께 확대.
/// 창마다 손대지 않도록 App 에서 Window.Loaded 전역 핸들러로 자동 적용한다.
/// </summary>
public static class UiScaler
{
    private static double _scale = 1.0;

    /// <summary>현재 배율 (0.8~2.0 로 제한).</summary>
    public static double Scale
    {
        get => _scale;
        set => _scale = Math.Clamp(value, 0.8, 2.0);
    }

    /// <summary>한 창에 현재 배율 적용 — 콘텐츠를 확대하고, 기본 크기도 배율만큼 키워 잘리지 않게.</summary>
    public static void Apply(Window w)
    {
        if (w.Content is not FrameworkElement root) return;

        if (Math.Abs(_scale - 1.0) < 0.001)
        {
            root.LayoutTransform = Transform.Identity;
            return;
        }

        root.LayoutTransform = new ScaleTransform(_scale, _scale);

        // 콘텐츠가 커진 만큼 창도 키운다 (이미 배율 적용된 창은 건너뜀)
        if (w.Tag as string == "scaled") return;
        w.Tag = "scaled";
        foreach (var (cur, set) in new (double, Action<double>)[]
        {
            (w.Width, v => w.Width = v), (w.Height, v => w.Height = v),
            (w.MinWidth, v => w.MinWidth = v), (w.MinHeight, v => w.MinHeight = v),
        })
            if (!double.IsNaN(cur) && cur > 0) set(cur * _scale);
    }

    /// <summary>현재 열려 있는 모든 창에 즉시 재적용 (설정에서 배율 바꿨을 때).
    /// 변환은 갱신하되, 이미 확대된 창은 크기를 다시 키우지 않는다(중복 확대 방지 — 완전 반영은 재시작).</summary>
    public static void ApplyToAll()
    {
        foreach (Window w in Application.Current.Windows) Apply(w);
    }
}
