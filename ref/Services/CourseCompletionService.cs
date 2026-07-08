using WindowsInput;
using WindowsInput.Native;
using DooClick.Core;
using DooClick.Utils;
using OpenCvSharp;
using Point = System.Drawing.Point;

namespace DooClick.Services;

/// <summary>
/// 강의 이수처리 서비스 (설문, 문제풀이, 이수처리, 나이스 전송)
/// AutomationEngine과 FullAutomation에서 공유 사용
/// </summary>
public class CourseCompletionService
{
    private readonly ImageMatcher _imageMatcher;
    private readonly InputSimulator _inputSimulator;
    private readonly Random _random = new();

    public event Action<string>? OnStatusChanged;

    /// <summary>
    /// 퀴즈 감지 시 외부 알림 이벤트 (Form1 퀴즈 탭 동기화용)
    /// </summary>
    public event Action? OnQuizDetected;

    public CourseCompletionService(ImageMatcher imageMatcher)
    {
        _imageMatcher = imageMatcher;
        _inputSimulator = new InputSimulator();
    }

    private void RaiseStatus(string message)
    {
        OnStatusChanged?.Invoke(message);
        Logger.Info($"[이수처리] {message}");
    }

    /// <summary>
    /// 편리모드에서 창이 화면에 다시 나타났으면 숨김 복원
    /// Chrome 페이지 전환 시 창 위치가 복원되는 문제 방지
    /// </summary>
    private void EnsureWindowHidden(IntPtr hwnd, bool convenienceMode)
    {
        if (!convenienceMode || hwnd == IntPtr.Zero) return;

        Win32Api.GetWindowRect(hwnd, out var rect);
        if (rect.Top > Config.OffScreenDetectThreshold)
        {
            Logger.Warning($"[이수처리] 편리모드 창이 다시 보임 (Y={rect.Top}) → 다시 숨김");
            Win32Api.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, Config.OffScreenY, 0, 0,
                Win32Api.SWP_NOSIZE | Win32Api.SWP_NOZORDER);
        }
    }

    /// <summary>
    /// 강의 이수처리 전체 프로세스 (설문 → 문제풀이 → 이수처리 → 나이스 전송)
    /// </summary>
    public async Task HandleCourseCompletionAsync(IntPtr browserHwnd, int scale, bool convenienceMode, CancellationToken ct)
    {
        Logger.Info("=== 강의 이수처리 시작 ===");

        // 1. 강의실 버튼 클릭
        await ClickClassroomButtonAsync(browserHwnd, scale, convenienceMode, ct);
        await Task.Delay(500, ct);
        EnsureWindowHidden(browserHwnd, convenienceMode); // 페이지 전환 후 창 숨김 확인

        // 2. 설문 처리
        RaiseStatus("설문 처리 중...");
        await HandleSurveyAsync(browserHwnd, scale, convenienceMode, ct);
        EnsureWindowHidden(browserHwnd, convenienceMode); // 설문 완료 후 창 숨김 확인

        // 3. 강의실 버튼 다시 클릭
        await ClickClassroomButtonAsync(browserHwnd, scale, convenienceMode, ct);
        await Task.Delay(500, ct);
        EnsureWindowHidden(browserHwnd, convenienceMode); // 페이지 전환 후 창 숨김 확인

        // 4. 문제풀이 처리
        RaiseStatus("문제풀이 확인 중...");
        bool testDone = await HandleTestAsync(browserHwnd, scale, convenienceMode, ct);
        EnsureWindowHidden(browserHwnd, convenienceMode); // 문제풀이 후 창 숨김 확인

        // 5. 문제풀이 후 강의실 버튼
        if (testDone)
        {
            await ClickClassroomButtonAsync(browserHwnd, scale, convenienceMode, ct);
            await Task.Delay(500, ct);
            EnsureWindowHidden(browserHwnd, convenienceMode);
        }

        // 6. 이수처리 버튼
        RaiseStatus("이수처리 버튼 찾는 중...");
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(browserHwnd, convenienceMode);
            var completeBtn = _imageMatcher.FindTemplateColor(screen, "course_complete.png", scale, 0.8);

            if (completeBtn.Found)
            {
                RaiseStatus("이수처리 버튼 클릭...");
                await AutomationHelper.ClickAtAsync(completeBtn.Center, browserHwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);
                if (convenienceMode)
                    ConvenienceMode.PostKey(browserHwnd, Win32Api.VK_RETURN);
                else
                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                await Task.Delay(500, ct);
                break;
            }

            await Task.Delay(300, ct);
        }

        // 7. 나이스 전송
        RaiseStatus("나이스 전송 버튼 찾는 중...");
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(browserHwnd, convenienceMode);
            var neisBtn = _imageMatcher.FindTemplateColor(screen, "neis_send.png", scale, 0.8);

            if (neisBtn.Found)
            {
                RaiseStatus("나이스 전송 버튼 클릭...");
                await AutomationHelper.ClickAtAsync(neisBtn.Center, browserHwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);
                if (convenienceMode)
                    ConvenienceMode.PostKey(browserHwnd, Win32Api.VK_RETURN);
                else
                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                await Task.Delay(500, ct);
                RaiseStatus("나이스 전송 완료!");
                Logger.Info("나이스 전송 완료");
                break;
            }

            await Task.Delay(1000, ct);
        }

        Logger.Info("=== 강의 이수처리 완료 ===");
    }

    /// <summary>
    /// 강의 창 닫고 다음 강의 열기
    /// </summary>
    /// <returns>새 강의실 창 hwnd (없으면 IntPtr.Zero)</returns>
    public async Task<IntPtr> OpenNextCourseAsync(IntPtr browserHwnd, int scale, bool convenienceMode, CancellationToken ct, IntPtr classroomHwnd = default)
    {
        Logger.Info("=== 다음 강의 열기 ===");

        // 1. 현재 강의 창 닫기
        RaiseStatus("강의 창 닫는 중...");
        await CloseLectureWindowAsync(browserHwnd, scale, convenienceMode, ct, classroomHwnd);
        await Task.Delay(1000, ct);

        // 2. 다음 강의 찾기 및 클릭 (study_continue.png / study_agree.png 사용)
        RaiseStatus("다음 강의 찾는 중...");
        for (int i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(browserHwnd, convenienceMode);
            using var grayScreen = new Mat();
            Cv2.CvtColor(screen, grayScreen, ColorConversionCodes.BGR2GRAY);

            // 이어보기 버튼 우선 (창 제목 "강의실"로 열림 → 제목 검색 가능)
            var continueBtn = _imageMatcher.FindTemplateFromGray(grayScreen, "study_continue.png", scale, 0.8);
            if (continueBtn.Found)
            {
                RaiseStatus("이어보기 클릭...");
                await AutomationHelper.ClickAtAsync(continueBtn.Center, browserHwnd, convenienceMode, _inputSimulator);
                await Task.Delay(2000, ct);
                Logger.Info("다음 강의 열기 완료 (이어보기)");

                // 이어보기는 "강의실" 제목으로 열리므로 제목 검색으로 찾기
                IntPtr continueHwnd = IntPtr.Zero;
                for (int w = 0; w < 30; w++)
                {
                    ct.ThrowIfCancellationRequested();
                    continueHwnd = Win32Api.FindWindowByTitle("강의실");
                    if (continueHwnd != IntPtr.Zero) break;
                    await Task.Delay(1000, ct);
                }
                return continueHwnd;
            }

            // 학습하기 버튼 → 새 창 감지 + start_study 클릭 필요
            var agreeBtn = _imageMatcher.FindTemplateFromGray(grayScreen, "study_agree.png", scale, 0.8);
            if (agreeBtn.Found)
            {
                RaiseStatus("학습하기 클릭...");
                var existingWindows = Win32Api.GetAllChromeWindows();
                await AutomationHelper.ClickAtAsync(agreeBtn.Center, browserHwnd, convenienceMode, _inputSimulator);

                // 새 Chrome 창 대기 (10초)
                RaiseStatus("새 창 대기 중...");
                IntPtr newHwnd = await WaitForNewChromeWindowAsync(existingWindows, 10, convenienceMode, ct);

                if (newHwnd != IntPtr.Zero)
                {
                    // 새 창에서 start_study.png 최상단 클릭 (재시도 루프가 로딩 대기 겸함)
                    RaiseStatus("학습시작 버튼 찾는 중...");
                    await ClickStartStudyInWindowAsync(newHwnd, scale, convenienceMode, ct);
                    Logger.Info("다음 강의 열기 완료 (학습하기)");
                    return newHwnd; // 이 창이 강의실
                }

                Logger.Warning("학습하기 후 새 창 미발견");
                return IntPtr.Zero;
            }

            // 스크롤 시도
            if (i % 5 == 4)
            {
                if (convenienceMode)
                    ConvenienceMode.PostKey(browserHwnd, Win32Api.VK_NEXT);
                else
                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.NEXT);
                await Task.Delay(500, ct);
            }

            await Task.Delay(500, ct);
        }

        Logger.Warning("다음 강의 찾기 실패");
        return IntPtr.Zero;
    }

    #region Private Methods

    private async Task ClickClassroomButtonAsync(IntPtr hwnd, int scale, bool convenienceMode, CancellationToken ct)
    {
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(hwnd, convenienceMode);
            var classroomBtn = _imageMatcher.FindTemplate(screen, "classroom_btn.png", scale, 0.8);

            if (classroomBtn.Found)
            {
                await AutomationHelper.ClickAtAsync(classroomBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                return;
            }

            await Task.Delay(500, ct);
        }

        Logger.Warning("강의실 버튼 찾기 실패");
    }

    /// <summary>
    /// 설문 캡처: hwnd가 유효하면 항상 창 캡처(PrintWindow) 우선 사용
    /// 전체 화면 캡처(1920x1080)보다 창 캡처가 템플릿 매칭 신뢰도가 높음
    /// </summary>
    private Mat CaptureSurveyScreen(IntPtr hwnd, bool convenienceMode)
    {
        if (hwnd != IntPtr.Zero)
        {
            var windowCapture = ScreenCapture.CaptureWindow(hwnd);
            if (windowCapture != null && !windowCapture.Empty())
            {
                Logger.Debug($"[설문] 창 캡처: {windowCapture.Width}x{windowCapture.Height}");
                return windowCapture;
            }
            Logger.Warning("[설문] 창 캡처 실패 → 전체 화면 캡처로 폴백");
        }
        return ScreenCapture.CaptureScreen();
    }

    private async Task HandleSurveyAsync(IntPtr hwnd, int scale, bool convenienceMode, CancellationToken ct)
    {
        Logger.Info($"설문 처리 시작 (hwnd=0x{hwnd:X}, convenienceMode={convenienceMode})");

        // 설문 메뉴 찾기
        bool surveyMenuFound = false;
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = CaptureSurveyScreen(hwnd, convenienceMode);
            using var grayScreen = new Mat();
            Cv2.CvtColor(screen, grayScreen, ColorConversionCodes.BGR2GRAY);
            var surveyMenu = _imageMatcher.FindTemplateFromGray(grayScreen, "survey.png", scale, 0.7);

            // test.png와 test-1.png 모두 체크 (시험중/응시가능 둘 다 테스트 버튼)
            var testMenu = _imageMatcher.FindTemplateFromGray(grayScreen, "test.png", scale, 0.7);
            var testMenu1 = _imageMatcher.FindTemplateFromGray(grayScreen, "test-1.png", scale, 0.7);
            // 둘 중 신뢰도 높은 쪽 사용
            var bestTest = (testMenu1.Found && testMenu1.Confidence > testMenu.Confidence) ? testMenu1 : testMenu;

            if (surveyMenu.Found && surveyMenu.Confidence >= 0.85)
            {
                // survey.png가 test/test-1 버튼을 오탐할 수 있으므로 비교
                if (bestTest.Found && surveyMenu.Confidence <= bestTest.Confidence + 0.05)
                {
                    Logger.Debug($"테스트 버튼으로 판단 - 설문 없음 (survey={surveyMenu.Confidence:F3}, test={bestTest.Confidence:F3})");
                    return;
                }

                RaiseStatus("설문 메뉴 클릭...");
                await AutomationHelper.ClickAtAsync(surveyMenu.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                EnsureWindowHidden(hwnd, convenienceMode); // 설문 페이지 전환 후 창 숨김 확인
                surveyMenuFound = true;
                break;
            }

            await Task.Delay(500, ct);
        }

        if (!surveyMenuFound)
        {
            Logger.Debug("설문 메뉴 없음");
            return;
        }

        // 설문시작 버튼 클릭
        for (int i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = CaptureSurveyScreen(hwnd, convenienceMode);
            var startBtn = _imageMatcher.FindTemplate(screen, "survey_start.png", scale, 0.85);

            if (startBtn.Found)
            {
                RaiseStatus("설문시작 버튼 클릭...");
                await AutomationHelper.ClickAtAsync(startBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                break;
            }

            await Task.Delay(500, ct);
        }

        // 설문 작성 반복 (최대 50번)
        for (int q = 0; q < 50; q++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = CaptureSurveyScreen(hwnd, convenienceMode);

            // 첫 설문 화면 디버그 저장
            if (q == 0)
            {
                try
                {
                    var debugPath = Path.Combine(Logger.LogFolder, "debug_survey.png");
                    Cv2.ImWrite(debugPath, screen);
                    Logger.Info($"[설문] 디버그 화면 저장: {debugPath} ({screen.Width}x{screen.Height})");
                }
                catch { }
            }

            // 설문 옵션 찾기 (checkbox.png / radio_button.png 검출)
            var checkboxes = _imageMatcher.FindAllTemplates(screen, "checkbox.png", scale, 0.85);
            var radioButtons = _imageMatcher.FindAllTemplates(screen, "radio_button.png", scale, 0.85);
            var allOptions = checkboxes.Concat(radioButtons).Where(r => r.Found).ToList();

            Logger.Info($"[설문 {q+1}] checkbox={checkboxes.Count(r => r.Found)}개, radio={radioButtons.Count(r => r.Found)}개, 총={allOptions.Count}개 (scale={scale})");
            foreach (var opt in allOptions)
                Logger.Debug($"[설문]   옵션: ({opt.Center.X},{opt.Center.Y}) score={opt.Confidence:F3}");

            if (allOptions.Count > 0)
            {
                // 랜덤 옵션 선택
                var selected = allOptions[_random.Next(allOptions.Count)];
                RaiseStatus($"설문 항목 선택 ({allOptions.Count}개 중 1개)...");
                await AutomationHelper.ClickAtAsync(selected.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(300, ct);
            }
            else
            {
                Logger.Warning($"[설문 {q+1}] 라디오/체크박스 검출 실패 (화면 {screen?.Width}x{screen?.Height})");
            }

            // 제출/다음 버튼 (같은 화면에서 그레이 1회 변환)
            using var graySurvey = new Mat();
            Cv2.CvtColor(screen, graySurvey, ColorConversionCodes.BGR2GRAY);
            var nextBtn = _imageMatcher.FindTemplateFromGray(graySurvey, "next_button.png", scale, 0.85);
            var submitBtn = _imageMatcher.FindTemplateFromGray(graySurvey, "survey_submit.png", scale, 0.85);

            if (submitBtn.Found && submitBtn.Confidence > nextBtn.Confidence)
            {
                RaiseStatus("설문 제출...");
                await AutomationHelper.ClickAtAsync(submitBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);
                Logger.Info("설문 제출 완료");
                break;
            }
            else if (nextBtn.Found)
            {
                RaiseStatus("설문 다음으로...");
                await AutomationHelper.ClickAtAsync(nextBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);
            }
            else
            {
                await Task.Delay(300, ct);
            }
        }
    }

    private async Task<bool> HandleTestAsync(IntPtr hwnd, int scale, bool convenienceMode, CancellationToken ct)
    {
        Logger.Info("문제풀이 처리 시작");

        // 테스트 메뉴 찾기 (test.png + test-1.png 복수 검색)
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(hwnd, convenienceMode);

            // test.png 또는 test-1.png 중 가장 높은 신뢰도
            var (testMenu, _, _) = _imageMatcher.FindAnyTemplate(
                screen, new[] { "test.png", "test-1.png" }, scale, 0.85);

            if (testMenu.Found)
            {
                RaiseStatus("문제풀이 메뉴 클릭...");
                await AutomationHelper.ClickAtAsync(testMenu.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                EnsureWindowHidden(hwnd, convenienceMode); // 시험 페이지 전환 후 창 숨김 확인

                // 퀴즈 감지 이벤트 발생 (Form1 퀴즈 탭 동기화)
                OnQuizDetected?.Invoke();

                // QuizSolver AI 기반 문제풀이 진행
                await SolveTestWithQuizSolverAsync(hwnd, scale, convenienceMode, ct);
                return true;
            }

            await Task.Delay(500, ct);
        }

        Logger.Debug("문제풀이 메뉴 없음");
        return false;
    }

    /// <summary>
    /// QuizSolver를 사용한 AI 기반 문제풀이
    /// </summary>
    private async Task SolveTestWithQuizSolverAsync(IntPtr hwnd, int scale, bool convenienceMode, CancellationToken ct)
    {
        // 테스트 시작 버튼 (start_test.png + start-test-2.png 폴백)
        for (int i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(hwnd, convenienceMode);
            var (startBtn, _, _) = _imageMatcher.FindAnyTemplate(
                screen, new[] { "start_test.png", "start-test-2.png" }, scale, 0.85);

            if (startBtn.Found)
            {
                RaiseStatus("문제풀이 시작...");
                await AutomationHelper.ClickAtAsync(startBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                EnsureWindowHidden(hwnd, convenienceMode); // 시험 시작 후 창 숨김 확인
                break;
            }

            await Task.Delay(500, ct);
        }

        // QuizSolver AI 풀이 시도
        if (Config.ValidateApiKey())
        {
            Logger.Info($"QuizSolver AI 풀이 시작 (편리모드: {convenienceMode})");
            RaiseStatus("AI 문제풀이 중...");

            try
            {
                // 편리모드: ConvenienceQuizSolver (PostMessage 클릭 + PrintWindow 캡처)
                // 일반모드: QuizSolver (물리 마우스 + CopyFromScreen 캡처)
                if (convenienceMode)
                {
                    var convSolver = new ConvenienceQuizSolver(_imageMatcher, hwnd);
                    convSolver.OnStatusChanged += msg => RaiseStatus($"[퀴즈] {msg}");

                    var solverTcs = new TaskCompletionSource<bool>();
                    convSolver.OnCompleted += success => solverTcs.TrySetResult(success);

                    convSolver.Start();

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

                    try
                    {
                        await solverTcs.Task.WaitAsync(timeoutCts.Token);
                        Logger.Info("ConvenienceQuizSolver AI 풀이 완료");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Logger.Warning("ConvenienceQuizSolver 타임아웃 (10분) - 정지");
                        convSolver.Stop();
                    }

                    convSolver.Dispose();
                }
                else
                {
                    using var quizSolver = new QuizSolver(_imageMatcher);
                    quizSolver.OnStatusChanged += msg => RaiseStatus($"[퀴즈] {msg}");

                    var solverTcs = new TaskCompletionSource<bool>();
                    quizSolver.OnCompleted += success => solverTcs.TrySetResult(success);

                    quizSolver.Start();

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

                    try
                    {
                        await solverTcs.Task.WaitAsync(timeoutCts.Token);
                        Logger.Info("QuizSolver AI 풀이 완료");
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        Logger.Warning("QuizSolver 타임아웃 (10분) - 정지");
                        quizSolver.Stop();
                    }
                }

                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"QuizSolver 오류: {ex.Message}");
                RaiseStatus("AI 풀이 실패 - 기본 방식으로 전환");
            }
        }
        else
        {
            Logger.Warning("API 키 없음 - 기본 방식으로 문제풀이");
        }

        // API 키 없거나 QuizSolver 실패 시: 기본 방식 (checkbox/radio 랜덤 선택)
        for (int q = 0; q < 30; q++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(hwnd, convenienceMode);

            // checkbox/radio_button 검출하여 랜덤 선택
            var radioButtons = _imageMatcher.FindAllTemplates(screen, "radio_button.png", scale, 0.85);
            var checkboxes = _imageMatcher.FindAllTemplates(screen, "checkbox.png", scale, 0.85);
            var allOptions = radioButtons.Concat(checkboxes).Where(r => r.Found).ToList();

            if (allOptions.Count > 0)
            {
                var selected = allOptions[_random.Next(allOptions.Count)];
                RaiseStatus($"문제 {q + 1}: 보기 선택 ({allOptions.Count}개 중 랜덤)");
                await AutomationHelper.ClickAtAsync(selected.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);
            }

            // 제출/다음 버튼 (같은 화면에서 그레이 1회 변환)
            using var grayTest = new Mat();
            Cv2.CvtColor(screen, grayTest, ColorConversionCodes.BGR2GRAY);
            var submitBtn = _imageMatcher.FindTemplateFromGray(grayTest, "submit.png", scale, 0.85);
            var nextBtn = _imageMatcher.FindTemplateFromGray(grayTest, "next_button.png", scale, 0.85);

            if (submitBtn.Found && submitBtn.Confidence > nextBtn.Confidence)
            {
                RaiseStatus("문제풀이 제출...");
                await AutomationHelper.ClickAtAsync(submitBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);

                // 답변 미선택 오류 확인 (엔터 전에 체크)
                using var errScreen1 = AutomationHelper.CaptureScreen(hwnd, convenienceMode);
                var errCheck1 = _imageMatcher.FindTemplate(errScreen1, "test_error.png", scale, 0.7);
                if (errCheck1.Found)
                {
                    Logger.Warning("답변 미선택 오류 - 엔터 후 재시도");
                    RaiseStatus("[오류] 답변 미선택 - 재시도");
                    if (convenienceMode)
                        ConvenienceMode.PostKey(hwnd, Win32Api.VK_RETURN);
                    else
                        _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                    await Task.Delay(300, ct);
                    q--;
                    continue;
                }

                if (convenienceMode)
                    ConvenienceMode.PostKey(hwnd, Win32Api.VK_RETURN);
                else
                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                await Task.Delay(1000, ct);
                Logger.Info("문제풀이 제출 완료");
                break;
            }
            else if (nextBtn.Found)
            {
                await AutomationHelper.ClickAtAsync(nextBtn.Center, hwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);

                // 답변 미선택 오류 확인
                using var errScreen2 = AutomationHelper.CaptureScreen(hwnd, convenienceMode);
                var errCheck2 = _imageMatcher.FindTemplate(errScreen2, "test_error.png", scale, 0.7);
                if (errCheck2.Found)
                {
                    Logger.Warning("답변 미선택 오류 - 엔터 후 재시도");
                    RaiseStatus("[오류] 답변 미선택 - 재시도");
                    if (convenienceMode)
                        ConvenienceMode.PostKey(hwnd, Win32Api.VK_RETURN);
                    else
                        _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                    await Task.Delay(300, ct);
                    q--;
                    continue;
                }
            }
            else
            {
                await Task.Delay(300, ct);
            }
        }
    }

    /// <summary>
    /// 새로 생긴 Chrome 창 대기
    /// </summary>
    private async Task<IntPtr> WaitForNewChromeWindowAsync(HashSet<IntPtr> existingWindows, int timeoutSeconds, bool convenienceMode, CancellationToken ct)
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

                if (convenienceMode)
                {
                    HiddenWindowsManager.Instance.HideWindow(hwnd);
                    Logger.Info("편리모드: 새 창 즉시 숨김");
                }

                return hwnd;
            }

            await Task.Delay(100, ct);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 창에서 start_study.png 찾아서 최상단(Y 최소) 클릭
    /// </summary>
    private async Task ClickStartStudyInWindowAsync(IntPtr windowHwnd, int scale, bool convenienceMode, CancellationToken ct)
    {
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(windowHwnd, convenienceMode);
            var allStartStudy = _imageMatcher.FindAllTemplates(screen, "start_study.png", scale, 0.8);

            if (allStartStudy.Count > 0)
            {
                var topmost = allStartStudy.OrderBy(m => m.Location.Y).First();
                Logger.Info($"start_study.png {allStartStudy.Count}개 발견, 최상단 클릭 (Y={topmost.Location.Y})");
                AutomationHelper.ClickAt(topmost.Center, windowHwnd, convenienceMode, _inputSimulator);
                await Task.Delay(1000, ct);
                return;
            }

            await Task.Delay(500, ct);
        }

        Logger.Warning("start_study.png를 찾지 못함");
    }

    private async Task CloseLectureWindowAsync(IntPtr browserHwnd, int scale, bool convenienceMode, CancellationToken ct, IntPtr classroomHwnd = default)
    {
        // 전달받은 핸들 우선 사용, 없으면 제목으로 검색
        var lectureHwnd = classroomHwnd != IntPtr.Zero ? classroomHwnd : Win32Api.FindWindowByTitle("강의실");
        if (lectureHwnd == IntPtr.Zero)
        {
            Logger.Warning("강의실 창을 찾을 수 없음 - 이미 닫혔을 수 있음");
            return;
        }

        Logger.Info($"강의실 창 닫기: hwnd={lectureHwnd} (전달받음: {classroomHwnd != IntPtr.Zero})");

        // 닫기 버튼 찾기 (popup_x.png 사용)
        for (int i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(lectureHwnd, convenienceMode);
            var closeBtn = _imageMatcher.FindTemplate(screen, "popup_x.png", scale, 0.8);

            if (closeBtn.Found)
            {
                await AutomationHelper.ClickAtAsync(closeBtn.Center, lectureHwnd, convenienceMode, _inputSimulator);
                await Task.Delay(500, ct);

                // 확인 버튼 처리
                using var screen2 = AutomationHelper.CaptureScreen(lectureHwnd, convenienceMode);
                var confirmBtn = _imageMatcher.FindTemplate(screen2, "3.png", scale, 0.8);
                if (confirmBtn.Found)
                {
                    await AutomationHelper.ClickAtAsync(confirmBtn.Center, lectureHwnd, convenienceMode, _inputSimulator);
                }

                return;
            }

            await Task.Delay(500, ct);
        }

        // popup_x.png 못 찾으면 창 닫기
        Logger.Info("닫기 버튼 못 찾음 - WM_CLOSE/Alt+F4 사용");
        if (convenienceMode)
        {
            // 편리모드: WM_CLOSE 메시지 전송
            Win32Api.CloseWindow(lectureHwnd);
            await Task.Delay(500, ct);
            ConvenienceMode.PostKey(lectureHwnd, Win32Api.VK_RETURN);
            await Task.Delay(500, ct);
        }
        else
        {
            Win32Api.SetForegroundWindow(lectureHwnd);
            await Task.Delay(200, ct);
            _inputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.LMENU, VirtualKeyCode.F4);
            await Task.Delay(500, ct);
            _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
            await Task.Delay(500, ct);
        }
    }

    #endregion
}
