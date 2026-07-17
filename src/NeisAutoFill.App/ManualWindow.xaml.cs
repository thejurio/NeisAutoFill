using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NeisAutoFill.App.Services;

namespace NeisAutoFill.App;

/// <summary>
/// 내장 사용설명서 팝업 (WebView2). 슬라이드 원본 크기(1020×740)+타이틀바에 맞춘 기본 크기.
/// WebView2 런타임이 없는 PC 는 InitializeAsync 가 false 를 반환 — 호출 측이 브라우저로 폴백.
/// </summary>
public partial class ManualWindow : Window
{
    public ManualWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) =>
            BtnMax.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    /// <summary>WebView2 초기화 + 설명서 로드. 런타임 미설치 등 실패 시 false.</summary>
    public async Task<bool> InitializeAsync(string manualPath)
    {
        try
        {
            // 사용자 데이터 폴더를 %AppData% 로 — Program Files 설치 시 쓰기 권한 문제 방지
            var env = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppPaths.Root, "webview2"));
            await Web.EnsureCoreWebView2Async(env);

            var s = Web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = true;   // 어르신 확대 허용 (Ctrl+휠)

            Web.CoreWebView2.Navigate(new Uri(manualPath).AbsoluteUri);
            return true;
        }
        catch
        {
            return false;   // WebView2 런타임 없음 → 호출 측이 기본 브라우저로 폴백
        }
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
