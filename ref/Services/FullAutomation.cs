using DooClick.Core;
using DooClick.Utils;

namespace DooClick.Services;

/// <summary>
/// 완전 자동화 서비스 (예약자동화)
/// 로그인 → 강의 선택 → 자동 수강 → 이수처리 → 다음 강의 반복
/// 분리된 서비스들(LoginService, NextCourseService, CourseCompletionService, AutomationEngine)을 조합하여 실행
/// </summary>
public class FullAutomation : IDisposable
{
    private readonly string _templateFolder;
    private readonly string _portalUrl;
    private ImageMatcher? _sharedImageMatcher;

    // 분리된 서비스들
    private LoginService? _loginService;
    private NextCourseService? _nextCourseService;
    private CourseCompletionService? _completionService;
    private AutomationEngine? _automationEngine;

    private CancellationTokenSource? _cts;
    private Task? _runningTask;
    private bool _disposed;
    private bool _convenienceMode;
    private IntPtr _browserHwnd;

    // 공유 창 관리자
    private readonly HiddenWindowsManager _windowsManager = HiddenWindowsManager.Instance;

    public event Action<string>? OnStatusChanged;
    public event Action<bool, string>? OnCompleted;
    public event Action<int>? OnChapterDetected;

    public bool IsRunning => _runningTask != null && !_runningTask.IsCompleted;

    public FullAutomation(string templateFolder, string portalUrl = "https://www.jbstudy.kr/")
    {
        _templateFolder = templateFolder;
        _portalUrl = portalUrl;
    }

    /// <summary>
    /// 완전 자동화 시작
    /// </summary>
    public void Start(string username, string password, bool autoNextCourse = true, bool convenienceMode = false)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _runningTask = Task.Run(() => RunFullAutomation(username, password, autoNextCourse, convenienceMode, _cts.Token));
    }

    /// <summary>
    /// 완전 자동화 중지
    /// </summary>
    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        _automationEngine?.Stop();
    }

    /// <summary>
    /// 완전 자동화 재개 (현재 열린 창에서 이어서)
    /// </summary>
    public void Resume(bool autoNextCourse = true, bool convenienceMode = false)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _runningTask = Task.Run(() => RunResumeAutomation(autoNextCourse, convenienceMode, _cts.Token));
    }

    /// <summary>
    /// 메인 자동화 로직
    /// </summary>
    private async Task RunFullAutomation(string username, string password, bool autoNextCourse, bool convenienceMode, CancellationToken ct)
    {
        var startTime = DateTime.Now;
        _convenienceMode = convenienceMode;

        Logger.Info($"=== 완전 자동화 시작 (편리모드: {convenienceMode}) ===");
        RaiseStatus($"완전 자동화 시작... (편리모드: {(convenienceMode ? "O" : "X")})");

        int scale = Win32Api.GetDisplayScale();
        RaiseStatus($"화면 배율: {scale}%");

        try
        {
            // 서비스 초기화
            InitializeServices();

            // 1. Chrome 실행 및 로그인 (LoginService 사용)
            ct.ThrowIfCancellationRequested();
            if (!await _loginService!.LaunchAndLoginAsync(username, password, scale, convenienceMode, ct))
            {
                RaiseStatus("[오류] 로그인 실패");
                OnCompleted?.Invoke(false, "로그인 실패");
                return;
            }

            _browserHwnd = _loginService.BrowserHwnd;
            _nextCourseService!.SetBrowserContext(_browserHwnd, convenienceMode);

            await Task.Delay(1000, ct);

            // 2. 수강과정 페이지로 이동 (NextCourseService 사용)
            ct.ThrowIfCancellationRequested();
            RaiseStatus("수강과정 페이지로 이동 중...");

            if (!await _nextCourseService.NavigateToMyClassroomAsync(scale, ct))
            {
                RaiseStatus("[오류] 수강과정 페이지 이동 실패");
                OnCompleted?.Invoke(false, "수강과정 페이지 이동 실패");
                return;
            }

            await Task.Delay(1000, ct);

            // 3. 강의 반복 처리
            await RunCourseLoop(autoNextCourse, convenienceMode, scale, ct, username, password);

            var elapsed = DateTime.Now - startTime;
            RaiseStatus($"완전 자동화 종료 (총 {elapsed.TotalMinutes:F1}분 소요)");
            OnCompleted?.Invoke(true, "정상 종료");
        }
        catch (OperationCanceledException)
        {
            RaiseStatus("완전 자동화가 중지되었습니다.");
            OnCompleted?.Invoke(false, "사용자 중지");
        }
        catch (Exception ex)
        {
            Logger.Error("완전 자동화 오류", ex);
            RaiseStatus($"[오류] {ex.Message}");
            OnCompleted?.Invoke(false, ex.Message);
        }
        finally
        {
            CleanupConvenienceMode();
            var totalElapsed = DateTime.Now - startTime;
            Logger.Info($"=== 완전 자동화 종료 (총 {totalElapsed.TotalMinutes:F1}분) ===");
        }
    }

    /// <summary>
    /// 재개 자동화 로직 (로그인 건너뛰고 현재 상태에서 시작)
    /// </summary>
    private async Task RunResumeAutomation(bool autoNextCourse, bool convenienceMode, CancellationToken ct)
    {
        var startTime = DateTime.Now;
        _convenienceMode = convenienceMode;

        Logger.Info($"=== 완전 자동화 재개 (편리모드: {convenienceMode}) ===");
        RaiseStatus("완전 자동화 재개 중...");

        int scale = Win32Api.GetDisplayScale();

        try
        {
            // 서비스 초기화
            InitializeServices();

            // 1. 현재 열린 브라우저 창 찾기
            _browserHwnd = AutomationHelper.FindBrowserWindow();
            if (_browserHwnd != IntPtr.Zero)
            {
                Logger.Info($"브라우저 창 발견: hwnd={_browserHwnd}");
                if (convenienceMode && !_windowsManager.IsHidden(_browserHwnd))
                {
                    _windowsManager.HideWindow(_browserHwnd);
                }
                _nextCourseService!.SetBrowserContext(_browserHwnd, convenienceMode);
            }

            // 2. 강의실 창 확인
            var classroomHwnd = Win32Api.FindWindowByTitle("강의실");
            if (classroomHwnd != IntPtr.Zero)
            {
                // 강의실 창이 있으면 바로 자동화 시작
                Logger.Info($"강의실 창 발견: hwnd={classroomHwnd}");
                RaiseStatus("강의실 창 발견 - 자동화 재개");

                if (convenienceMode && !_windowsManager.IsHidden(classroomHwnd))
                {
                    _windowsManager.HideWindow(classroomHwnd);
                }

                await Task.Delay(1000, ct);

                var exitReason = await RunAutomationEngineAsync(convenienceMode, ct, classroomHwnd: classroomHwnd);
                Logger.Info($"AutomationEngine 종료: {exitReason}");

                if (exitReason == ExitReason.DailyLimitReached)
                {
                    RaiseStatus("=== 일일 한도 도달 ===");
                    OnCompleted?.Invoke(true, "일일 한도 도달");
                    return;
                }

                if (exitReason == ExitReason.CourseCompleted && autoNextCourse)
                {
                    RaiseStatus("강의 완료 - 다음 강의 확인 중...");

                    // 편리모드: 엔진이 Deactivate()로 창을 (0,0)에 복원했으므로 실제 위치 기준으로 다시 숨김
                    // (HiddenWindowsManager 딕셔너리 상태와 무관하게 실제 창 위치 확인)
                    if (convenienceMode && classroomHwnd != IntPtr.Zero)
                    {
                        Win32Api.GetWindowRect(classroomHwnd, out var afterRect);
                        if (afterRect.Top > Config.OffScreenDetectThreshold)
                        {
                            _windowsManager.HideWindow(classroomHwnd);
                            Logger.Info($"강의 완료 후 강의실 창 다시 숨김 (Y={afterRect.Top} → {Config.OffScreenY})");
                        }
                    }

                    await _completionService!.HandleCourseCompletionAsync(classroomHwnd, scale, convenienceMode, ct);

                    // 강의실 닫기 + 다음 강의 찾기
                    var nextHwnd = await _completionService.OpenNextCourseAsync(_browserHwnd, scale, convenienceMode, ct, classroomHwnd);
                    if (nextHwnd != IntPtr.Zero)
                    {
                        if (convenienceMode)
                        {
                            await Task.Delay(1000, ct);
                            _windowsManager.HideAllRelatedWindows();
                        }
                        await Task.Delay(1000, ct);
                        // 다음 강의 hwnd를 가지고 루프 진입
                        await RunCourseLoop(autoNextCourse, convenienceMode, scale, ct, initialClassroomHwnd: nextHwnd);
                    }
                    else
                    {
                        // 다음 강의 없음 → 수강과정에서 재탐색
                        if (await _nextCourseService!.NavigateToMyClassroomAsync(scale, ct))
                            await RunCourseLoop(autoNextCourse, convenienceMode, scale, ct);
                    }
                }
            }
            else
            {
                // 강의실 창 없으면 수강과정 페이지에서 시작
                RaiseStatus("강의실 창 없음 - 수강과정에서 재개");
                Logger.Info("강의실 창 없음, 수강과정 페이지로 이동");

                if (!await _nextCourseService!.NavigateToMyClassroomAsync(scale, ct))
                {
                    RaiseStatus("[오류] 수강과정 페이지 이동 실패");
                    OnCompleted?.Invoke(false, "수강과정 페이지 이동 실패");
                    return;
                }

                // 이후 강의 루프
                await RunCourseLoop(autoNextCourse, convenienceMode, scale, ct);
            }

            var elapsed = DateTime.Now - startTime;
            RaiseStatus($"완전 자동화 종료 (총 {elapsed.TotalMinutes:F1}분 소요)");
            OnCompleted?.Invoke(true, "정상 종료");
        }
        catch (OperationCanceledException)
        {
            RaiseStatus("완전 자동화가 중지되었습니다.");
            OnCompleted?.Invoke(false, "사용자 중지");
        }
        catch (Exception ex)
        {
            Logger.Error("완전 자동화 재개 오류", ex);
            RaiseStatus($"[오류] {ex.Message}");
            OnCompleted?.Invoke(false, ex.Message);
        }
        finally
        {
            CleanupConvenienceMode();
            Logger.Info($"=== 완전 자동화 재개 종료 ===");
        }
    }

    /// <summary>
    /// 강의 반복 루프
    /// </summary>
    private async Task RunCourseLoop(bool autoNextCourse, bool convenienceMode, int scale, CancellationToken ct, string? username = null, string? password = null, IntPtr initialClassroomHwnd = default)
    {
        int courseCount = 0;
        IntPtr classroomHwnd = initialClassroomHwnd;

        while (!ct.IsCancellationRequested)
        {
            courseCount++;
            RaiseStatus($"=== {courseCount}번째 강의 처리 ===");
            Logger.Info($"=== 강의 #{courseCount} 처리 시작 ===");

            if (classroomHwnd == IntPtr.Zero)
            {
                // 수강과정 페이지에서 강의 찾기
                _nextCourseService!.ScrollUp(5);
                await Task.Delay(500, ct);

                RaiseStatus("수강 가능한 강의 찾는 중...");
                var (btnType, location) = _nextCourseService.FindAvailableCourse(scale);

                if (location == null)
                {
                    RaiseStatus("수강 가능한 강의가 없습니다. (모두 완료)");
                    Logger.Info("수강 가능한 강의 없음 - 루프 종료");
                    break;
                }

                // 강의 열기 및 강의실 창 대기
                ct.ThrowIfCancellationRequested();
                classroomHwnd = await _nextCourseService.OpenCourseAndWaitForClassroomAsync(
                    btnType!, location.Value, scale, ct);

                if (classroomHwnd == IntPtr.Zero)
                {
                    RaiseStatus("[경고] 강의실 창이 열리지 않음, 다음 강의로...");
                    continue;
                }
            }

            await Task.Delay(2000, ct);

            // 자동 수강 시작 (AutomationEngine)
            ct.ThrowIfCancellationRequested();
            RaiseStatus("=== 자동 수강 시작 ===");
            Logger.Info("===== AutomationEngine 시작 =====");

            var exitReason = await RunAutomationEngineAsync(convenienceMode, ct, username, password, classroomHwnd);
            Logger.Info($"AutomationEngine 종료 원인: {exitReason}");

            // 외부 중지 체크
            if (ct.IsCancellationRequested)
            {
                RaiseStatus("사용자가 중지했습니다.");
                break;
            }

            // 세션 만료 → 처음부터 재시작 (로그인 → 강좌선택 → 수강)
            if (exitReason == ExitReason.SessionExpired)
            {
                Logger.Warning("세션 만료로 처음부터 재시작");
                RaiseStatus("세션 만료 - 처음부터 재시작합니다...");

                // 기존 창 정리
                classroomHwnd = IntPtr.Zero;
                CleanupConvenienceMode();

                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    Logger.Error("자격증명 없음 - 재시작 불가");
                    RaiseStatus("[오류] 자격증명 없음 - 수동 로그인 필요");
                    break;
                }

                await Task.Delay(2000, ct);

                // Chrome 재실행 + 로그인
                if (!await _loginService!.LaunchAndLoginAsync(username, password, scale, convenienceMode, ct))
                {
                    Logger.Error("재로그인 실패");
                    RaiseStatus("[오류] 재로그인 실패");
                    break;
                }

                _browserHwnd = _loginService.BrowserHwnd;
                _nextCourseService!.SetBrowserContext(_browserHwnd, convenienceMode);
                await Task.Delay(1000, ct);

                // 수강과정 페이지 이동
                if (!await _nextCourseService.NavigateToMyClassroomAsync(scale, ct))
                {
                    Logger.Error("수강과정 페이지 이동 실패");
                    break;
                }

                await Task.Delay(1000, ct);
                Logger.Info("세션 만료 재시작 완료 - 강의 루프 계속");
                continue;
            }

            // 일일 한도 도달 → 종료
            if (exitReason == ExitReason.DailyLimitReached)
            {
                RaiseStatus("=== 일일 한도 도달 - 자동화 종료 ===");
                Logger.Info("일일 한도 도달, 종료");
                OnCompleted?.Invoke(true, "일일 한도 도달 (15차시)");
                return;
            }

            // 강의 완료 → 후처리 (CourseCompletionService)
            if (exitReason == ExitReason.CourseCompleted)
            {
                RaiseStatus("=== 강의 완료 후 처리 ===");

                // 편리모드: 엔진이 Deactivate()로 창을 (0,0)에 복원했으므로 실제 위치 기준으로 다시 숨김
                if (convenienceMode && classroomHwnd != IntPtr.Zero)
                {
                    Win32Api.GetWindowRect(classroomHwnd, out var afterRect);
                    if (afterRect.Top > Config.OffScreenDetectThreshold)
                    {
                        _windowsManager.HideWindow(classroomHwnd);
                        Logger.Info($"강의 완료 후 강의실 창 다시 숨김 (Y={afterRect.Top} → {Config.OffScreenY})");
                    }
                }

                await Task.Delay(2000, ct);

                await _completionService!.HandleCourseCompletionAsync(classroomHwnd, scale, convenienceMode, ct);

                if (!autoNextCourse)
                {
                    RaiseStatus("강의 완료! (다음 강의 자동 시작 비활성화)");
                    break;
                }

                // 다음 강의 열기 (classroomHwnd 전달하여 강의실 창 정확히 닫기)
                var nextClassroomHwnd = await _completionService.OpenNextCourseAsync(_browserHwnd, scale, convenienceMode, ct, classroomHwnd);
                if (nextClassroomHwnd != IntPtr.Zero)
                {
                    // 다음 강의실 hwnd 확보됨 → 바로 AutomationEngine 시작
                    classroomHwnd = nextClassroomHwnd;

                    if (convenienceMode)
                    {
                        await Task.Delay(1000, ct);
                        _windowsManager.HideAllRelatedWindows();
                    }

                    Logger.Info($"다음 강의 열기 성공 - hwnd={nextClassroomHwnd}");
                    await Task.Delay(1000, ct);
                    continue;
                }
                else
                {
                    classroomHwnd = IntPtr.Zero;
                    RaiseStatus("다음 강의 없음 - 수강과정 페이지로...");
                    if (!await _nextCourseService!.NavigateToMyClassroomAsync(scale, ct))
                    {
                        break;
                    }
                    await Task.Delay(1000, ct);
                    continue;
                }
            }

            // 알 수 없는 종료
            classroomHwnd = IntPtr.Zero;
            RaiseStatus("[경고] 예상치 못한 종료. 다음 강의 시도...");
            Logger.Warning($"알 수 없는 종료 원인: {exitReason}");
            await Task.Delay(2000, ct);

            if (!await _nextCourseService!.NavigateToMyClassroomAsync(scale, ct))
            {
                Logger.Error("복구 실패 - 루프 종료");
                break;
            }
        }
    }

    #region 서비스 관리

    private void InitializeServices()
    {
        // 공유 ImageMatcher 생성 (템플릿 캐시 공유)
        _sharedImageMatcher = new ImageMatcher(_templateFolder);

        // LoginService 초기화
        _loginService = new LoginService(_sharedImageMatcher, _portalUrl);
        _loginService.OnStatusChanged += RaiseStatus;

        // NextCourseService 초기화
        _nextCourseService = new NextCourseService(_sharedImageMatcher);
        _nextCourseService.OnStatusChanged += RaiseStatus;

        // CourseCompletionService 초기화
        _completionService = new CourseCompletionService(_sharedImageMatcher);
        _completionService.OnStatusChanged += RaiseStatus;

        Logger.Info("FullAutomation 서비스들 초기화 완료 (공유 ImageMatcher)");
    }

    private async Task<ExitReason> RunAutomationEngineAsync(bool convenienceMode, CancellationToken ct, string? username = null, string? password = null, IntPtr classroomHwnd = default)
    {
        // 이전 인스턴스 정리 (반복 호출 시 리소스 누수 방지)
        _automationEngine?.Dispose();
        _automationEngine = new AutomationEngine(_templateFolder, "강의실");
        _automationEngine.ClassroomHwnd = classroomHwnd;
        _automationEngine.KeepConvenienceModeOnExit = convenienceMode;

        // 세션 만료 시 재로그인용 자격증명 설정
        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            _automationEngine.GetCredentials = () => (username!, password!);
            Logger.Info("AutomationEngine에 자격증명 설정 완료");
        }

        var tcs = new TaskCompletionSource<ExitReason>();

        _automationEngine.OnStatusChanged += (_, e) => RaiseStatus(e.Message);
        _automationEngine.OnChapterDetected += (ch) => OnChapterDetected?.Invoke(ch);
        _automationEngine.OnCompleted += (_, e) =>
        {
            tcs.TrySetResult(e.ExitReason ?? ExitReason.Unknown);
        };

        _automationEngine.Start(convenienceMode: convenienceMode);

        // 취소 처리
        using var registration = ct.Register(() =>
        {
            _automationEngine.Stop();
            tcs.TrySetResult(ExitReason.UserStopped);
        });

        return await tcs.Task;
    }

    #endregion

    #region 유틸리티

    private void CleanupConvenienceMode()
    {
        if (_convenienceMode)
        {
            _windowsManager.ShowAllWindows();
            _convenienceMode = false;
        }
    }

    private void RaiseStatus(string message)
    {
        OnStatusChanged?.Invoke(message);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _automationEngine?.Dispose();
        _sharedImageMatcher?.Dispose();
        _cts?.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
