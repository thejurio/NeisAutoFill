using System.Runtime.InteropServices;
using System.Text;

namespace DooClick.Utils;

/// <summary>
/// Windows API P/Invoke 래퍼
/// </summary>
public static class Win32Api
{
    #region User32.dll

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd); // 최소화 상태 확인

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    #endregion

    #region Gdi32.dll

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    #endregion

    #region 상수

    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;

    // 가상 키 코드
    public const uint VK_BACK = 0x08;      // Backspace
    public const uint VK_TAB = 0x09;       // Tab
    public const uint VK_RETURN = 0x0D;    // Enter
    public const uint VK_CONTROL = 0x11;   // Ctrl
    public const uint VK_0 = 0x30;          // 0 키
    public const uint VK_R = 0x52;          // R 키
    public const uint VK_PRIOR = 0x21;     // PageUp
    public const uint VK_NEXT = 0x22;      // PageDown
    public const uint VK_END = 0x23;       // End

    // 창 메시지
    public const uint WM_CLOSE = 0x0010;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;

    public const int SRCCOPY = 0x00CC0020;
    public const int LOGPIXELSX = 88;
    public const int LOGPIXELSY = 90;

    #endregion

    #region 구조체

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    #endregion

    #region 대리자

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    #endregion

    #region 헬퍼 메서드

    /// <summary>
    /// 창 제목으로 창 핸들 찾기
    /// </summary>
    public static IntPtr FindWindowByTitle(string titleContains)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            int length = GetWindowTextLength(hWnd);
            if (length == 0) return true;

            var sb = new StringBuilder(length + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            string title = sb.ToString();

            if (title.Contains(titleContains))
            {
                found = hWnd;
                return false; // 찾았으면 중단
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 창 제목 가져오기
    /// </summary>
    public static string GetWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length == 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>
    /// 화면 배율 가져오기
    /// </summary>
    public static int GetDisplayScale()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        int dpiX = GetDeviceCaps(hdc, LOGPIXELSX);
        ReleaseDC(IntPtr.Zero, hdc);

        return (int)(dpiX / 96.0 * 100);
    }

    /// <summary>
    /// 좌표를 LPARAM으로 변환
    /// </summary>
    public static IntPtr MakeLParam(int x, int y)
    {
        return (IntPtr)((y << 16) | (x & 0xFFFF));
    }

    /// <summary>
    /// 창 닫기
    /// </summary>
    public static void CloseWindow(IntPtr hWnd)
    {
        if (hWnd != IntPtr.Zero && IsWindow(hWnd))
        {
            PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>
    /// 현재 열려 있는 모든 Chrome 창 핸들 가져오기
    /// </summary>
    public static HashSet<IntPtr> GetAllChromeWindows()
    {
        var windows = new HashSet<IntPtr>();

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (!string.IsNullOrEmpty(title) && title.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(hWnd);
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// 기존 목록에 없는 새 Chrome 창 찾기
    /// </summary>
    public static IntPtr FindNewChromeWindow(HashSet<IntPtr> existingWindows)
    {
        IntPtr found = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (existingWindows.Contains(hWnd)) return true;

            string title = GetWindowTitle(hWnd);
            if (!string.IsNullOrEmpty(title) && title.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    #endregion

    #region 절전 방지

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;

    /// <summary>
    /// 절전모드 및 모니터 꺼짐 방지
    /// </summary>
    public static void PreventSleep()
    {
        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
    }

    /// <summary>
    /// 절전 방지 해제 (시스템 기본 동작 복원)
    /// </summary>
    public static void AllowSleep()
    {
        SetThreadExecutionState(ES_CONTINUOUS);
    }

    #endregion
}
