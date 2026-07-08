using System.Drawing;
using System.Drawing.Imaging;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using DooClick.Utils;

namespace DooClick.Services;

/// <summary>
/// 화면 캡처 서비스
/// </summary>
public static class ScreenCapture
{
    /// <summary>
    /// 전체 화면 캡처 (모든 모니터 포함)
    /// </summary>
    public static Mat CaptureScreen()
    {
        // 모든 모니터를 포함하는 가상 화면 영역
        var bounds = SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        return BitmapConverter.ToMat(bitmap);
    }

    /// <summary>
    /// 특정 영역 캡처
    /// </summary>
    public static Mat CaptureRegion(Rectangle region)
    {
        using var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(region.Location, System.Drawing.Point.Empty, region.Size);
        return BitmapConverter.ToMat(bitmap);
    }

    /// <summary>
    /// 특정 창 캡처 (PrintWindow API 사용 - 숨겨진 창도 가능)
    /// </summary>
    public static Mat? CaptureWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;

        Win32Api.GetWindowRect(hWnd, out var rect);
        int width = rect.Width;
        int height = rect.Height;

        if (width <= 0 || height <= 0)
        {
            Logger.Warning($"CaptureWindow: 창 크기 이상 ({width}x{height})");
            return null;
        }

        Logger.Debug($"CaptureWindow: 창 위치 ({rect.Left},{rect.Top}), 크기 ({width}x{height})");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        IntPtr hdc = graphics.GetHdc();

        bool success;
        try
        {
            // PrintWindow로 창 캡처 (PW_RENDERFULLCONTENT = 2)
            success = Win32Api.PrintWindow(hWnd, hdc, 2);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        if (!success)
        {
            Logger.Warning("CaptureWindow: PrintWindow 실패, BitBlt 시도");
            // PrintWindow 실패 시 BitBlt 시도 (Python 버전과 동일)
            using var g2 = Graphics.FromImage(bitmap);
            IntPtr hdcDest = g2.GetHdc();
            IntPtr hdcSrc = Win32Api.GetDC(hWnd);
            try
            {
                Win32Api.BitBlt(hdcDest, 0, 0, width, height, hdcSrc, 0, 0, Win32Api.SRCCOPY);
            }
            finally
            {
                g2.ReleaseHdc(hdcDest);
                Win32Api.ReleaseDC(hWnd, hdcSrc);
            }
        }

        var mat = BitmapConverter.ToMat(bitmap);
        Logger.Debug($"CaptureWindow: 캡처 완료 ({mat.Width}x{mat.Height})");
        return mat;
    }

    /// <summary>
    /// Mat을 Bitmap으로 변환
    /// </summary>
    public static Bitmap MatToBitmap(Mat mat)
    {
        return BitmapConverter.ToBitmap(mat);
    }
}
