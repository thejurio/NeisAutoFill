using System.Drawing;

namespace DooClick.Utils;

/// <summary>
/// 숨긴 창들을 전역적으로 관리하는 싱글톤 매니저
/// Form1과 FullAutomation 간에 공유됨
/// </summary>
public sealed class HiddenWindowsManager
{
    private static readonly Lazy<HiddenWindowsManager> _instance = new(() => new HiddenWindowsManager());
    public static HiddenWindowsManager Instance => _instance.Value;

    private readonly Dictionary<IntPtr, Point> _hiddenWindows = new();
    private readonly object _lock = new();

    // 편리모드 관련 키워드 (Python과 동일)
    public static readonly string[] ConvenienceKeywords =
    {
        "강의실", "jbstudy", "전북교육연수포털", "전북특별자치도교육청", "Chrome"
    };

    private HiddenWindowsManager() { }

    /// <summary>
    /// 현재 숨겨진 창 개수
    /// </summary>
    public int HiddenCount
    {
        get
        {
            lock (_lock)
            {
                return _hiddenWindows.Count;
            }
        }
    }

    /// <summary>
    /// 창 숨기기 (화면 밖으로 이동)
    /// </summary>
    public bool HideWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;

        lock (_lock)
        {
            if (_hiddenWindows.ContainsKey(hwnd))
                return false;

            try
            {
                Win32Api.GetWindowRect(hwnd, out var rect);
                var originalPos = new Point(rect.Left, rect.Top);

                // 화면 밖으로 이동
                Win32Api.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, Config.OffScreenY, 0, 0,
                    Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);

                _hiddenWindows[hwnd] = originalPos;
                Logger.Debug($"[HiddenWindowsManager] 창 숨김: hwnd={hwnd}, 원래위치=({originalPos.X},{originalPos.Y})");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error($"[HiddenWindowsManager] 창 숨기기 오류: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// 특정 창 복구
    /// </summary>
    public bool ShowWindow(IntPtr hwnd)
    {
        lock (_lock)
        {
            if (!_hiddenWindows.TryGetValue(hwnd, out var pos))
                return false;

            try
            {
                if (Win32Api.IsWindow(hwnd))
                {
                    Win32Api.SetWindowPos(hwnd, IntPtr.Zero, pos.X, pos.Y, 0, 0,
                        Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);
                    Logger.Debug($"[HiddenWindowsManager] 창 복구: hwnd={hwnd}, 위치=({pos.X},{pos.Y})");
                }
                _hiddenWindows.Remove(hwnd);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warning($"[HiddenWindowsManager] 창 복구 실패: {ex.Message}");
                _hiddenWindows.Remove(hwnd);
                return false;
            }
        }
    }

    /// <summary>
    /// 모든 숨긴 창 복구
    /// </summary>
    public void ShowAllWindows()
    {
        lock (_lock)
        {
            foreach (var (hwnd, pos) in _hiddenWindows)
            {
                try
                {
                    if (Win32Api.IsWindow(hwnd))
                    {
                        // 원래 위치가 화면 밖이면 (숨김 상태로 시작한 경우) 화면 안으로 복원
                        int restoreX = pos.X;
                        int restoreY = pos.Y;

                        if (restoreX < Config.OffScreenRangeMax || restoreY < Config.OffScreenRangeMax)
                        {
                            restoreX = 0;
                            restoreY = 0;
                            Logger.Debug($"[HiddenWindowsManager] 원래 위치가 화면 밖 ({pos.X},{pos.Y}) → 기본 위치로 복원");
                        }

                        Win32Api.SetWindowPos(hwnd, IntPtr.Zero, restoreX, restoreY, 0, 0,
                            Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);
                        Logger.Debug($"[HiddenWindowsManager] 창 복구: hwnd={hwnd}, 위치=({restoreX},{restoreY})");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"[HiddenWindowsManager] 창 복구 실패: {ex.Message}");
                }
            }

            int count = _hiddenWindows.Count;
            _hiddenWindows.Clear();
            Logger.Info($"[HiddenWindowsManager] 모든 창 복구 완료: {count}개");
        }
    }

    /// <summary>
    /// 모든 관련 창 숨기기 (키워드 기반)
    /// </summary>
    public int HideAllRelatedWindows()
    {
        int hiddenCount = 0;

        Win32Api.EnumWindows((hwnd, lParam) =>
        {
            if (Win32Api.IsWindowVisible(hwnd))
            {
                string title = Win32Api.GetWindowTitle(hwnd);
                if (!string.IsNullOrEmpty(title))
                {
                    foreach (var keyword in ConvenienceKeywords)
                    {
                        if (title.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        {
                            if (HideWindow(hwnd))
                            {
                                hiddenCount++;
                                Logger.Debug($"[HiddenWindowsManager] 창 숨김: \"{title}\"");
                            }
                            break;
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        Logger.Info($"[HiddenWindowsManager] HideAllRelatedWindows: {hiddenCount}개 창 숨김");
        return hiddenCount;
    }

    /// <summary>
    /// 창이 이미 숨겨져 있는지 확인
    /// </summary>
    public bool IsHidden(IntPtr hwnd)
    {
        lock (_lock)
        {
            return _hiddenWindows.ContainsKey(hwnd);
        }
    }

    /// <summary>
    /// 화면 밖에 있는 모든 관련 창 복원 (추적 여부와 관계없이)
    /// </summary>
    public int RestoreAllOffScreenWindows()
    {
        int restoredCount = 0;

        // 먼저 추적 중인 창들 복원
        ShowAllWindows();

        // 추가로 화면 밖에 있는 관련 창들 찾아서 복원
        // 단, 최소화된 창(-32000)은 건드리지 않음
        Win32Api.EnumWindows((hwnd, lParam) =>
        {
            if (Win32Api.IsWindow(hwnd))
            {
                string title = Win32Api.GetWindowTitle(hwnd);
                if (!string.IsNullOrEmpty(title))
                {
                    // 관련 창인지 확인 (제목 앞부분만 체크 - 경로 제외)
                    // "강의실", "jbstudy" 등 특정 키워드로 시작하거나 포함하는 창만
                    bool isRelated = title.Contains("강의실") ||
                                     title.Contains("jbstudy") ||
                                     title.Contains("전북교육연수포털") ||
                                     title.Contains("Chrome");

                    if (isRelated)
                    {
                        Win32Api.GetWindowRect(hwnd, out var rect);
                        // 편리모드로 숨긴 창만 복원 (최소화 -32000 제외)
                        if (rect.Top < Config.OffScreenRangeMax && rect.Top > Config.OffScreenRangeMin)
                        {
                            try
                            {
                                Win32Api.SetWindowPos(hwnd, IntPtr.Zero, 100, 100, 0, 0,
                                    Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);
                                restoredCount++;
                                Logger.Info($"[HiddenWindowsManager] 화면 밖 창 복원: \"{title}\" (Y={rect.Top} → 100)");
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"[HiddenWindowsManager] 창 복원 실패: {ex.Message}");
                            }
                        }
                    }
                }
            }
            return true;
        }, IntPtr.Zero);

        if (restoredCount > 0)
        {
            Logger.Info($"[HiddenWindowsManager] RestoreAllOffScreenWindows: {restoredCount}개 추가 복원");
        }

        return restoredCount;
    }
}
