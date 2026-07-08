using DooClick.Core;
using DooClick.Utils;
using OpenCvSharp;
using WindowsInput;
using Point = System.Drawing.Point;

namespace DooClick.Services;

/// <summary>
/// 자동화 서비스 공통 헬퍼 (LoginService, NextCourseService, CourseCompletionService 중복 로직 통합)
/// </summary>
public static class AutomationHelper
{
    private static readonly string[] BrowserKeywords =
        { "jbstudy", "전북교육연수포털", "전북특별자치도교육청", "Chrome" };

    private static readonly string[] ChromePaths =
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Google\Chrome\Application\chrome.exe")
    };

    /// <summary>
    /// 화면 캡처 (편리모드: 창 캡처, 일반모드: 전체 화면)
    /// </summary>
    public static Mat CaptureScreen(IntPtr hwnd, bool convenienceMode)
    {
        if (convenienceMode && hwnd != IntPtr.Zero)
        {
            return ScreenCapture.CaptureWindow(hwnd) ?? ScreenCapture.CaptureScreen();
        }
        return ScreenCapture.CaptureScreen();
    }

    /// <summary>
    /// 지정 좌표 클릭 (편리모드: SendMessage, 일반모드: 마우스 이동)
    /// </summary>
    public static void ClickAt(Point center, IntPtr hwnd, bool convenienceMode, InputSimulator inputSimulator)
    {
        if (convenienceMode && hwnd != IntPtr.Zero)
        {
            var clientPos = ConvenienceMode.WindowToClient(hwnd, center.X, center.Y);
            ConvenienceMode.PostClick(hwnd, clientPos.X, clientPos.Y);
        }
        else
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen != null)
            {
                inputSimulator.Mouse.MoveMouseTo(
                    center.X * 65535 / screen.Bounds.Width,
                    center.Y * 65535 / screen.Bounds.Height);
                inputSimulator.Mouse.LeftButtonClick();
            }
        }
    }

    /// <summary>
    /// 지정 좌표 클릭 + 딜레이 (비동기)
    /// </summary>
    public static async Task ClickAtAsync(Point center, IntPtr hwnd, bool convenienceMode, InputSimulator inputSimulator, int delayMs = 200)
    {
        ClickAt(center, hwnd, convenienceMode, inputSimulator);
        if (delayMs > 0) await Task.Delay(delayMs);
    }

    /// <summary>
    /// 팝업 닫기 (popup_x.png, 3.png 검출)
    /// </summary>
    public static async Task ClosePopupsAsync(ImageMatcher imageMatcher, int scale, IntPtr hwnd, bool convenienceMode, InputSimulator inputSimulator, CancellationToken ct)
    {
        for (int i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = CaptureScreen(hwnd, convenienceMode);
            using var grayScreen = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(screen, grayScreen, OpenCvSharp.ColorConversionCodes.BGR2GRAY);

            var closeBtn = imageMatcher.FindTemplateFromGray(grayScreen, "popup_x.png", scale, 0.8);
            if (closeBtn.Found)
            {
                ClickAt(closeBtn.Center, hwnd, convenienceMode, inputSimulator);
                await Task.Delay(300, ct);
                continue;
            }

            var confirmBtn = imageMatcher.FindTemplateFromGray(grayScreen, "3.png", scale, 0.8);
            if (confirmBtn.Found)
            {
                ClickAt(confirmBtn.Center, hwnd, convenienceMode, inputSimulator);
                await Task.Delay(300, ct);
                continue;
            }

            break;
        }
    }

    /// <summary>
    /// 브라우저 창 찾기
    /// </summary>
    public static IntPtr FindBrowserWindow()
    {
        foreach (var keyword in BrowserKeywords)
        {
            var hwnd = Win32Api.FindWindowByTitle(keyword);
            if (hwnd != IntPtr.Zero)
                return hwnd;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Chrome 실행 경로 찾기
    /// </summary>
    public static string? FindChromePath()
    {
        return ChromePaths.FirstOrDefault(File.Exists);
    }
}
