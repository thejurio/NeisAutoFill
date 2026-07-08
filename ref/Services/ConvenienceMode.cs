using System.Runtime.InteropServices;
using DooClick.Utils;

namespace DooClick.Services;

/// <summary>
/// 편리모드 서비스 (모니터 밖 이동 + SendMessage 클릭)
/// </summary>
public class ConvenienceMode : IDisposable
{
    private IntPtr _targetHwnd;
    private Win32Api.RECT _originalRect;
    private bool _isActive;
    private bool _disposed;

    public bool IsActive => _isActive;
    public IntPtr TargetHwnd => _targetHwnd;

    /// <summary>
    /// 외부에서 편리모드 상태 설정 (창이 이미 이동된 경우)
    /// </summary>
    public void SetActiveState(IntPtr hwnd, bool active)
    {
        _targetHwnd = hwnd;
        _isActive = active;
        Logger.Debug($"편리모드 상태 설정: active={active}, hwnd={hwnd}");
    }

    // 화면 밖 위치 → Config.OffScreenY 사용

    /// <summary>
    /// 편리모드 활성화 (창을 화면 밖으로 이동)
    /// </summary>
    public bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            _targetHwnd = hwnd;

            // 현재 위치 저장
            Win32Api.GetWindowRect(hwnd, out _originalRect);

            // 화면 밖으로 이동
            Win32Api.SetWindowPos(
                hwnd, IntPtr.Zero,
                _originalRect.Left, Config.OffScreenY,
                0, 0,
                Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);

            _isActive = true;
            Logger.Info($"편리모드 활성화: hwnd={hwnd}, 원래위치=({_originalRect.Left}, {_originalRect.Top})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("편리모드 활성화 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// 편리모드 비활성화 (창을 원래 위치로 복귀)
    /// </summary>
    public bool Deactivate()
    {
        if (!_isActive || _targetHwnd == IntPtr.Zero) return false;

        try
        {
            // 원래 위치로 복귀
            Win32Api.SetWindowPos(
                _targetHwnd, IntPtr.Zero,
                _originalRect.Left, _originalRect.Top,
                0, 0,
                Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);

            _isActive = false;
            Logger.Info($"편리모드 비활성화: 복귀위치=({_originalRect.Left}, {_originalRect.Top})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("편리모드 비활성화 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// 창이 화면 밖에 있는지 확인
    /// </summary>
    public static bool IsWindowOffScreen(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            Win32Api.GetWindowRect(hwnd, out var rect);
            return rect.Top < Config.OffScreenDetectThreshold;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 화면 좌표를 클라이언트 좌표로 변환 (창 테두리와 타이틀바 고려)
    /// </summary>
    public static System.Drawing.Point ScreenToClient(IntPtr hwnd, int screenX, int screenY)
    {
        Win32Api.GetWindowRect(hwnd, out var windowRect);
        Win32Api.GetClientRect(hwnd, out var clientRect);

        int windowWidth = windowRect.Width;
        int windowHeight = windowRect.Height;
        int clientWidth = clientRect.Width;
        int clientHeight = clientRect.Height;

        // 테두리 두께 계산 (좌우 균등)
        int borderX = (windowWidth - clientWidth) / 2;
        // 타이틀바 높이 계산 (상단 = 전체높이 - 클라이언트높이 - 하단테두리)
        int titleBarY = windowHeight - clientHeight - borderX;

        int clientX = screenX - windowRect.Left - borderX;
        int clientY = screenY - windowRect.Top - titleBarY;

        return new System.Drawing.Point(clientX, clientY);
    }

    /// <summary>
    /// PrintWindow 캡처 이미지 좌표를 클라이언트 좌표로 변환
    /// (PrintWindow는 창 전체를 캡처하므로 타이틀바/테두리 오프셋만 빼면 됨)
    /// </summary>
    public static System.Drawing.Point WindowToClient(IntPtr hwnd, int windowX, int windowY)
    {
        Win32Api.GetWindowRect(hwnd, out var windowRect);
        Win32Api.GetClientRect(hwnd, out var clientRect);

        int windowWidth = windowRect.Width;
        int windowHeight = windowRect.Height;
        int clientWidth = clientRect.Width;
        int clientHeight = clientRect.Height;

        // 테두리 두께 계산
        int borderX = (windowWidth - clientWidth) / 2;
        // 타이틀바 높이 계산
        int titleBarY = windowHeight - clientHeight - borderX;

        int clientX = windowX - borderX;
        int clientY = windowY - titleBarY;

        Logger.Debug($"WindowToClient: window({windowX},{windowY}) → client({clientX},{clientY}) [border={borderX}, titleBar={titleBarY}]");

        return new System.Drawing.Point(clientX, clientY);
    }

    // 마우스 버튼 플래그
    private const int MK_LBUTTON = 0x0001;

    /// <summary>
    /// PostMessage로 더블클릭 (창이 화면 밖에 있어도 동작)
    /// </summary>
    public static bool PostDoubleClick(IntPtr hwnd, int clientX, int clientY)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var lParam = Win32Api.MakeLParam(clientX, clientY);

            // 더블클릭: Down → Up → DblClk → Up
            Win32Api.PostMessage(hwnd, Win32Api.WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
            Thread.Sleep(50);
            Win32Api.PostMessage(hwnd, Win32Api.WM_LBUTTONUP, IntPtr.Zero, lParam);
            Thread.Sleep(50);
            Win32Api.PostMessage(hwnd, Win32Api.WM_LBUTTONDBLCLK, (IntPtr)MK_LBUTTON, lParam);
            Thread.Sleep(50);
            Win32Api.PostMessage(hwnd, Win32Api.WM_LBUTTONUP, IntPtr.Zero, lParam);

            Logger.Debug($"PostDoubleClick: ({clientX}, {clientY})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("PostDoubleClick 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// SendMessage로 클릭 (창이 화면 밖에 있어도 동작)
    /// </summary>
    public static bool PostClick(IntPtr hwnd, int clientX, int clientY)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var lParam = Win32Api.MakeLParam(clientX, clientY);

            // 마우스 다운 → 업 (wParam에 MK_LBUTTON 필요)
            Win32Api.PostMessage(hwnd, Win32Api.WM_LBUTTONDOWN, (IntPtr)MK_LBUTTON, lParam);
            Thread.Sleep(50);
            Win32Api.PostMessage(hwnd, Win32Api.WM_LBUTTONUP, IntPtr.Zero, lParam);

            Logger.Debug($"PostClick: ({clientX}, {clientY})");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("PostClick 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// PostMessage로 Ctrl+키 조합 입력 (창이 화면 밖에 있어도 동작)
    /// </summary>
    public static bool PostCtrlKey(IntPtr hwnd, uint virtualKeyCode)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYDOWN, (IntPtr)Win32Api.VK_CONTROL, IntPtr.Zero);
            Thread.Sleep(30);
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYDOWN, (IntPtr)virtualKeyCode, IntPtr.Zero);
            Thread.Sleep(30);
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYUP, (IntPtr)virtualKeyCode, IntPtr.Zero);
            Thread.Sleep(30);
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYUP, (IntPtr)Win32Api.VK_CONTROL, IntPtr.Zero);

            Logger.Debug($"PostCtrlKey: Ctrl+0x{virtualKeyCode:X2}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("PostCtrlKey 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// PostMessage로 키 입력 (창이 화면 밖에 있어도 동작)
    /// </summary>
    public static bool PostKey(IntPtr hwnd, uint virtualKeyCode)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYDOWN, (IntPtr)virtualKeyCode, IntPtr.Zero);
            Thread.Sleep(50);
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYUP, (IntPtr)virtualKeyCode, IntPtr.Zero);

            Logger.Debug($"PostKey: VK=0x{virtualKeyCode:X2}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("PostKey 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// PostMessage로 텍스트 입력 (창이 화면 밖에 있어도 동작)
    /// </summary>
    public static bool PostText(IntPtr hwnd, string text)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(text)) return false;

        try
        {
            foreach (char c in text)
            {
                Win32Api.PostMessage(hwnd, Win32Api.WM_CHAR, (IntPtr)c, IntPtr.Zero);
                Thread.Sleep(10);
            }

            Logger.Debug($"PostText: {text.Length}자 입력");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("PostText 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// 포커스된 컨트롤에 텍스트 입력 (기존 텍스트 삭제 후 입력)
    /// PostMessage로는 Ctrl+A가 동작하지 않으므로 Backspace로 삭제
    /// </summary>
    public static bool PostTextWithClear(IntPtr hwnd, string text)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            // End 키로 커서를 끝으로 이동
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYDOWN, (IntPtr)Win32Api.VK_END, IntPtr.Zero);
            Thread.Sleep(20);
            Win32Api.PostMessage(hwnd, Win32Api.WM_KEYUP, (IntPtr)Win32Api.VK_END, IntPtr.Zero);
            Thread.Sleep(30);

            // Backspace 반복으로 기존 텍스트 삭제
            for (int i = 0; i < 30; i++)
            {
                Win32Api.PostMessage(hwnd, Win32Api.WM_KEYDOWN, (IntPtr)Win32Api.VK_BACK, IntPtr.Zero);
                Thread.Sleep(5);
                Win32Api.PostMessage(hwnd, Win32Api.WM_KEYUP, (IntPtr)Win32Api.VK_BACK, IntPtr.Zero);
                Thread.Sleep(5);
            }
            Thread.Sleep(50);

            // 텍스트 입력
            return PostText(hwnd, text);
        }
        catch (Exception ex)
        {
            Logger.Error("PostTextWithClear 실패", ex);
            return false;
        }
    }

    /// <summary>
    /// PageDown 키 전송
    /// </summary>
    public bool SendPageDown()
    {
        if (!_isActive || _targetHwnd == IntPtr.Zero) return false;
        return PostKey(_targetHwnd, Win32Api.VK_NEXT);
    }

    /// <summary>
    /// PrintWindow 캡처 이미지 좌표로 클릭 (자동 좌표 변환)
    /// 편리모드에서는 PrintWindow 캡처를 사용하므로 WindowToClient 사용
    /// </summary>
    public bool Click(int imageX, int imageY)
    {
        if (!_isActive || _targetHwnd == IntPtr.Zero)
        {
            Logger.Warning($"편리모드 클릭 실패: active={_isActive}, hwnd={_targetHwnd}");
            return false;
        }

        // PrintWindow 캡처 좌표 → 클라이언트 좌표
        var clientPos = WindowToClient(_targetHwnd, imageX, imageY);

        // 음수 좌표 체크 (창 영역 밖)
        if (clientPos.X < 0 || clientPos.Y < 0)
        {
            Logger.Warning($"편리모드 클릭 좌표가 음수: image({imageX},{imageY}) → client({clientPos.X},{clientPos.Y})");
        }

        Logger.Debug($"편리모드 클릭: image({imageX},{imageY}) → client({clientPos.X},{clientPos.Y})");
        return PostClick(_targetHwnd, clientPos.X, clientPos.Y);
    }

    public void Dispose()
    {
        if (_disposed) return;

        if (_isActive)
            Deactivate();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
