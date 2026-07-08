using System.Runtime.InteropServices;

namespace DooClick.Services;

/// <summary>
/// 전역 단축키 관리자 (F9: 시작, F10: 중지)
/// </summary>
public class HotkeyManager : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // 가상 키 코드
    private const uint VK_F9 = 0x78;
    private const uint VK_F10 = 0x79;

    // 단축키 ID
    private const int HOTKEY_START = 1;
    private const int HOTKEY_STOP = 2;

    // Windows 메시지
    public const int WM_HOTKEY = 0x0312;

    private readonly IntPtr _hwnd;
    private bool _registered;
    private bool _disposed;

    public event Action? OnStartPressed;
    public event Action? OnStopPressed;

    public HotkeyManager(IntPtr formHandle)
    {
        _hwnd = formHandle;
    }

    /// <summary>
    /// 단축키 등록
    /// </summary>
    public bool Register()
    {
        if (_registered) return true;

        try
        {
            var f9 = RegisterHotKey(_hwnd, HOTKEY_START, 0, VK_F9);
            var f10 = RegisterHotKey(_hwnd, HOTKEY_STOP, 0, VK_F10);

            _registered = f9 && f10;

            if (_registered)
                Logger.Info("단축키 등록 완료: F9(시작), F10(중지)");
            else
                Logger.Warning("단축키 등록 실패 - 다른 프로그램에서 사용 중일 수 있음");

            return _registered;
        }
        catch (Exception ex)
        {
            Logger.Error("단축키 등록 오류", ex);
            return false;
        }
    }

    /// <summary>
    /// 단축키 해제
    /// </summary>
    public void Unregister()
    {
        if (!_registered) return;

        UnregisterHotKey(_hwnd, HOTKEY_START);
        UnregisterHotKey(_hwnd, HOTKEY_STOP);
        _registered = false;

        Logger.Info("단축키 해제 완료");
    }

    /// <summary>
    /// WndProc에서 호출 - 단축키 메시지 처리
    /// </summary>
    public bool ProcessHotkey(int hotkeyId)
    {
        switch (hotkeyId)
        {
            case HOTKEY_START:
                Logger.Info("F9 단축키 감지");
                OnStartPressed?.Invoke();
                return true;

            case HOTKEY_STOP:
                Logger.Info("F10 단축키 감지");
                OnStopPressed?.Invoke();
                return true;

            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Unregister();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
