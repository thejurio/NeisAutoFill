using System.Windows;

namespace NeisAutoFill.App;

/// <summary>자동업데이트 진행 창 — 다운로드 %·적용 단계를 표시. 닫기 버튼 없음 (완료 시 재시작).</summary>
public partial class UpdateWindow : Window
{
    public UpdateWindow(string version)
    {
        InitializeComponent();
        TitleText.Text = $"v{version} 으로 업데이트 중";
    }

    /// <summary>진행 상태 갱신 (UI 스레드에서 호출). percent=null 이면 진행률 미상(움직이는 바).</summary>
    public void Report(string status, double? percent)
    {
        StatusText.Text = status;
        if (percent is { } p)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = p;
        }
        else Bar.IsIndeterminate = true;
    }
}
