using System.Drawing;
using WindowsInput;
using WindowsInput.Native;
using DooClick.Core;
using DooClick.Utils;
using OpenCvSharp;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace DooClick.Services;

/// <summary>
/// 다음 강의 탐색 서비스 (수강과정 페이지 이동, 강의 찾기, 강의실 열기)
/// </summary>
public class NextCourseService
{
    private readonly ImageMatcher _imageMatcher;
    private readonly InputSimulator _inputSimulator;
    private readonly HiddenWindowsManager _windowsManager = HiddenWindowsManager.Instance;

    private IntPtr _browserHwnd;
    private bool _convenienceMode;

    public event Action<string>? OnStatusChanged;

    public NextCourseService(ImageMatcher imageMatcher)
    {
        _imageMatcher = imageMatcher;
        _inputSimulator = new InputSimulator();
    }

    /// <summary>
    /// 브라우저 핸들 및 편리모드 설정
    /// </summary>
    public void SetBrowserContext(IntPtr browserHwnd, bool convenienceMode)
    {
        _browserHwnd = browserHwnd;
        _convenienceMode = convenienceMode;
    }

    private void RaiseStatus(string message)
    {
        OnStatusChanged?.Invoke(message);
        Logger.Info($"[강의탐색] {message}");
    }

    /// <summary>
    /// 수강과정 페이지로 이동
    /// </summary>
    public async Task<bool> NavigateToMyClassroomAsync(int scale, CancellationToken ct)
    {
        Logger.Info("수강과정 페이지로 이동 시도");

        await AutomationHelper.ClosePopupsAsync(_imageMatcher, scale, _browserHwnd, _convenienceMode, _inputSimulator, ct);

        // 수강과정 버튼 찾기 (페이지다운 3회까지)
        for (int attempt = 0; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode);
            var lecturesBtn = _imageMatcher.FindTemplate(screen, "lectures.png", scale, 0.8);

            if (lecturesBtn.Found)
            {
                RaiseStatus("수강과정 버튼 클릭...");
                AutomationHelper.ClickAt(lecturesBtn.Center, _browserHwnd, _convenienceMode, _inputSimulator);
                await Task.Delay(2000, ct);

                // 페이지 로딩 확인
                for (int i = 0; i < 20; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var checkScreen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode);
                    using var grayCheck = new Mat();
                    Cv2.CvtColor(checkScreen, grayCheck, ColorConversionCodes.BGR2GRAY);
                    var continueBtn = _imageMatcher.FindTemplateFromGray(grayCheck, "study_continue.png", scale, 0.8);
                    var startBtn = _imageMatcher.FindTemplateFromGray(grayCheck, "study_agree.png", scale, 0.8);

                    if (continueBtn.Found || startBtn.Found)
                    {
                        Logger.Info("수강과정 페이지 이동 성공");
                        RaiseStatus("수강과정 페이지 로드 완료!");
                        return true;
                    }

                    await Task.Delay(500, ct);
                }

                Logger.Info("수강과정 페이지 이동 (버튼 미확인)");
                return true;
            }

            if (attempt < 3)
            {
                RaiseStatus($"페이지다운 ({attempt + 1}/3)...");
                if (_convenienceMode)
                    ConvenienceMode.PostKey(_browserHwnd, Win32Api.VK_NEXT);
                else
                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.NEXT);
                await Task.Delay(600, ct);
            }
        }

        Logger.Error("수강과정 버튼 찾기 실패");
        return false;
    }

    /// <summary>
    /// 수강 가능한 강의 찾기
    /// </summary>
    /// <returns>(버튼타입, 위치) - 버튼타입: "continue"(이어보기), "start"(학습하기), null(없음)</returns>
    public (string? BtnType, Rectangle? Location) FindAvailableCourse(int scale)
    {
        using var screen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode);

        // 그레이 변환 1회로 여러 템플릿 검색 최적화
        using var grayScreen = new Mat();
        Cv2.CvtColor(screen, grayScreen, ColorConversionCodes.BGR2GRAY);

        // 이어보기 버튼 찾기 (우선)
        var continueBtn = _imageMatcher.FindTemplateFromGray(grayScreen, "study_continue.png", scale, 0.8);
        if (continueBtn.Found)
        {
            Logger.Debug("이어보기 버튼 발견");
            return ("continue", new Rectangle(continueBtn.Location, continueBtn.Size));
        }

        // 학습하기 버튼 찾기
        var startBtn = _imageMatcher.FindTemplateFromGray(grayScreen, "study_agree.png", scale, 0.8);
        if (startBtn.Found)
        {
            Logger.Debug("학습하기 버튼 발견");
            return ("start", new Rectangle(startBtn.Location, startBtn.Size));
        }

        return (null, null);
    }

    /// <summary>
    /// 강의 버튼 클릭 후 강의실 창 열기까지
    /// </summary>
    public async Task<IntPtr> OpenCourseAndWaitForClassroomAsync(string btnType, Rectangle location, int scale, CancellationToken ct)
    {
        string btnName = btnType == "continue" ? "이어보기" : "학습하기";

        // 학습하기: 클릭 전에 기존 Chrome 창 목록 저장 (새 창 감지용)
        HashSet<IntPtr>? existingWindows = null;
        if (btnType == "start")
        {
            existingWindows = Win32Api.GetAllChromeWindows();
            Logger.Info($"학습하기 - 기존 Chrome 창 {existingWindows.Count}개 기록");
        }

        // 버튼 클릭
        RaiseStatus($"'{btnName}' 버튼 클릭...");
        var center = new Point(location.X + location.Width / 2, location.Y + location.Height / 2);
        AutomationHelper.ClickAt(center, _browserHwnd, _convenienceMode, _inputSimulator);
        await Task.Delay(2000, ct);

        IntPtr classroomHwnd = IntPtr.Zero;

        if (btnType == "start" && existingWindows != null)
        {
            // === 학습하기 플로우 ===
            // 1. 학습하기 클릭 후 열리는 새 Chrome 창 대기
            RaiseStatus("새 창 대기 중...");
            var newHwnd = await WaitForNewChromeWindowAsync(existingWindows, 10, ct);

            if (newHwnd != IntPtr.Zero)
            {
                // 새 창에서 start_study.png 찾아서 최상단 클릭 (재시도 루프가 로딩 대기 겸함)
                RaiseStatus("학습시작 버튼 찾는 중...");
                await ClickStartStudyInWindowAsync(newHwnd, scale, ct);

                // start_study 클릭 후 해당 창이 강의실로 전환됨
                classroomHwnd = newHwnd;
            }
        }
        else
        {
            // === 이어보기 플로우 ===
            RaiseStatus("강의실 창 대기 중...");
            classroomHwnd = await WaitForClassroomWindowAsync(30, ct);

            if (classroomHwnd == IntPtr.Zero)
            {
                RaiseStatus("[경고] 강의실 창이 열리지 않음");
                Logger.Warning("강의실 창 미발견");

                await AutomationHelper.ClosePopupsAsync(_imageMatcher, scale, _browserHwnd, _convenienceMode, _inputSimulator, ct);
                await Task.Delay(1000, ct);
                classroomHwnd = await WaitForClassroomWindowAsync(10, ct);
            }
        }

        if (classroomHwnd != IntPtr.Zero)
        {
            string title = Win32Api.GetWindowTitle(classroomHwnd);
            RaiseStatus($"강의실 창 열림! ({title})");
            Logger.Info($"강의실 창 확정: hwnd={classroomHwnd}, 제목=\"{title}\"");

            // 편리모드: 모든 관련 창 숨기기
            if (_convenienceMode)
            {
                await Task.Delay(1000, ct);
                int hiddenCount = _windowsManager.HideAllRelatedWindows();
                RaiseStatus($"편리모드: {hiddenCount}개 창 숨김");
            }
        }

        return classroomHwnd;
    }

    /// <summary>
    /// 페이지 상단으로 스크롤
    /// </summary>
    public void ScrollUp(int times = 5)
    {
        for (int i = 0; i < times; i++)
        {
            if (_convenienceMode)
                ConvenienceMode.PostKey(_browserHwnd, Win32Api.VK_PRIOR);
            else
                _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.PRIOR);
            Thread.Sleep(100);
        }
    }

    #region Private Methods

    /// <summary>
    /// 새 창에서 start_study.png 찾아서 최상단(Y가 가장 작은) 클릭
    /// </summary>
    private async Task ClickStartStudyInWindowAsync(IntPtr windowHwnd, int scale, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(windowHwnd, _convenienceMode);
            var allStartStudy = _imageMatcher.FindAllTemplates(screen, "start_study.png", scale, 0.8);

            if (allStartStudy.Count > 0)
            {
                // 가장 위에 있는 것 (Y가 가장 작은 것) 선택
                var topmost = allStartStudy.OrderBy(m => m.Location.Y).First();
                Logger.Info($"start_study.png {allStartStudy.Count}개 발견, 최상단 클릭 (Y={topmost.Location.Y})");
                AutomationHelper.ClickAt(topmost.Center, windowHwnd, _convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                return;
            }

            await Task.Delay(500, ct);
        }

        Logger.Warning("start_study.png를 찾지 못함");
    }

    private async Task<IntPtr> WaitForClassroomWindowAsync(int timeoutSeconds, CancellationToken ct)
    {
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = Win32Api.FindWindowByTitle("강의실");
            if (hwnd != IntPtr.Zero)
            {
                Logger.Debug($"강의실 창 발견: hwnd={hwnd}");

                if (_convenienceMode)
                {
                    _windowsManager.HideWindow(hwnd);
                    Logger.Info("편리모드: 강의실 창 즉시 숨김");
                }

                return hwnd;
            }

            await Task.Delay(100, ct);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 새로 생긴 Chrome 창 대기 (학습하기용 - 창 제목 무관)
    /// </summary>
    private async Task<IntPtr> WaitForNewChromeWindowAsync(HashSet<IntPtr> existingWindows, int timeoutSeconds, CancellationToken ct)
    {
        var startTime = DateTime.Now;

        while ((DateTime.Now - startTime).TotalSeconds < timeoutSeconds)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = Win32Api.FindNewChromeWindow(existingWindows);
            if (hwnd != IntPtr.Zero)
            {
                string title = Win32Api.GetWindowTitle(hwnd);
                Logger.Info($"새 Chrome 창 발견: hwnd={hwnd}, 제목=\"{title}\"");

                if (_convenienceMode)
                {
                    _windowsManager.HideWindow(hwnd);
                    Logger.Info("편리모드: 새 강의 창 즉시 숨김");
                }

                return hwnd;
            }

            await Task.Delay(100, ct);
        }

        return IntPtr.Zero;
    }

    #endregion
}
