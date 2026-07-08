using System.Drawing;
using DooClick.Core;
using DooClick.Utils;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using WindowsInput;
using WindowsInput.Native;
using Point = System.Drawing.Point;

namespace DooClick.Services;

/// <summary>
/// AI 기반 퀴즈 자동 풀이
/// </summary>
public class QuizSolver : IDisposable
{
    private readonly ImageMatcher _imageMatcher;
    private readonly bool _ownsImageMatcher;
    private readonly GeminiSolver _geminiSolver;
    private readonly InputSimulator _inputSimulator;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private static readonly int SIDEBAR_WIDTH = Config.SidebarWidth;
    private const double BUTTON_CONFIDENCE = 0.85;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnCompleted;

    public bool IsRunning { get; private set; }

    public QuizSolver(string templateFolder)
    {
        _imageMatcher = new ImageMatcher(templateFolder);
        _ownsImageMatcher = true;
        _geminiSolver = new GeminiSolver();
        _inputSimulator = new InputSimulator();
    }

    /// <summary>
    /// 공유 ImageMatcher를 사용하는 생성자 (템플릿 캐시 공유, 메모리 절약)
    /// </summary>
    public QuizSolver(ImageMatcher sharedImageMatcher)
    {
        _imageMatcher = sharedImageMatcher;
        _ownsImageMatcher = false;
        _geminiSolver = new GeminiSolver();
        _inputSimulator = new InputSimulator();
    }

    /// <summary>
    /// 퀴즈 풀이 시작
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        IsRunning = true;

        Task.Run(async () =>
        {
            try
            {
                await SolveAllQuizzesAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                RaiseStatus("퀴즈 풀이가 중지되었습니다.");
            }
            catch (Exception ex)
            {
                Logger.Error("퀴즈 풀이 오류", ex);
                RaiseStatus($"오류: {ex.Message}");
            }
            finally
            {
                IsRunning = false;
                OnCompleted?.Invoke(true);
            }
        });
    }

    /// <summary>
    /// 퀴즈 풀이 중지
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        IsRunning = false;
    }

    /// <summary>
    /// 모든 퀴즈 풀이
    /// </summary>
    private async Task SolveAllQuizzesAsync(CancellationToken ct)
    {
        RaiseStatus("3초 후 퀴즈 풀이를 시작합니다...");

        // 3초 카운트다운
        for (int i = 3; i > 0; i--)
        {
            RaiseStatus($"{i}초 후 시작...");
            await Task.Delay(1000, ct);
        }

        int solvedCount = 0;
        const int maxQuestions = 30;
        int retryCount = 0;
        const int maxRetries = 3;

        for (int i = 0; i < maxQuestions; i++)
        {
            ct.ThrowIfCancellationRequested();

            RaiseStatus($"--- 문제 {i + 1} ---");
            Logger.Info($"문제 {i + 1} 풀이 시작");

            var (success, isComplete, needsRetry) = await SolveSingleQuizAsync(ct);

            // 답변 미선택 오류 → 같은 문제 재시도
            if (needsRetry && retryCount < maxRetries)
            {
                retryCount++;
                RaiseStatus($"답변 오류 - 재시도 ({retryCount}/{maxRetries})");
                Logger.Warning($"답변 미선택 오류 - 재시도 {retryCount}/{maxRetries}");
                i--;
                await Task.Delay(500, ct);
                continue;
            }
            retryCount = 0;

            if (success)
            {
                solvedCount++;
                Logger.Info($"문제 {i + 1} 풀이 성공");

                if (isComplete)
                {
                    RaiseStatus($"퀴즈 완료! {solvedCount}문제 풀이");
                    Logger.Info($"퀴즈 완료! 총 {solvedCount}문제");
                    break;
                }
            }
            else
            {
                // API 실패 등으로 답을 못 골랐으면 다음 버튼 누르지 않고 재시도
                Logger.Warning($"문제 {i + 1} 풀이 실패 - 다음 버튼 안 누르고 재시도");

                // 혹시 test_error 팝업이 떠있으면 엔터로 닫기
                if (await CheckTestErrorAsync(ct))
                {
                    Logger.Warning("test_error 팝업 감지 → 엔터로 닫음");
                }

                if (retryCount < maxRetries)
                {
                    retryCount++;
                    i--;
                    await Task.Delay(1000, ct);
                    continue;
                }
                // 재시도 한도 초과 시 다음 문제로 넘기기
                Logger.Warning($"문제 {i + 1} 재시도 한도 초과 - 스킵");
                retryCount = 0;
            }

            await Task.Delay(800, ct);
        }
    }

    /// <summary>
    /// 단일 퀴즈 풀이
    /// </summary>
    private async Task<(bool Success, bool IsComplete, bool NeedsRetry)> SolveSingleQuizAsync(CancellationToken ct)
    {
        try
        {
            // API 키 확인
            if (!Config.ValidateApiKey())
            {
                RaiseStatus("[오류] API 키가 설정되지 않았습니다.");
                return (false, false, false);
            }

            // 퀴즈 창 찾기
            var hWnd = Win32Api.FindWindowByTitle(Config.TargetWindowTitleQuiz);
            if (hWnd == IntPtr.Zero)
            {
                hWnd = Win32Api.FindWindowByTitle("강의실");
            }
            if (hWnd == IntPtr.Zero)
            {
                RaiseStatus("퀴즈 창을 찾을 수 없습니다.");
                return (false, false, false);
            }

            Win32Api.GetWindowRect(hWnd, out var windowRect);
            var quizRegion = GetQuizContentRegion(windowRect);

            // PageDown으로 문제 전체 보이게
            _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.NEXT);
            await Task.Delay(150, ct);

            // 스크린샷 캡처
            RaiseStatus("퀴즈 화면 캡처 중...");
            using var screenshot = CaptureRegion(quizRegion);
            if (screenshot == null)
            {
                RaiseStatus("화면 캡처 실패");
                return (false, false, false);
            }

            // Gemini에게 정답 요청
            RaiseStatus("AI 분석 중...");
            byte[] imageBytes = screenshot.ToBytes(".png");
            var result = await _geminiSolver.SolveQuizFromImageAsync(imageBytes);

            if (!result.Success)
            {
                RaiseStatus($"[오류] {result.Error}");

                // 모든 API 키 소진 → 팝업 + 풀이 중단
                if (result.ErrorCode == GeminiSolver.ERROR_ALL_KEYS_EXHAUSTED)
                {
                    MessageBox.Show(
                        "오늘 사용 가능한 API 사용량을 모두 소진했습니다.\n내일 다시 시도해주세요.",
                        "API 사용량 초과", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    throw new OperationCanceledException("API 키 소진");
                }

                return (false, false, false);
            }

            Logger.Info($"AI 응답: {result.Answer} (유형: {result.AnswerType})");
            RaiseStatus($"AI 정답: {result.Answer}");

            await Task.Delay(500, ct);

            // 라디오 버튼/체크박스 검출 및 클릭
            bool clicked = await ClickAnswerByDetectionAsync(screenshot, quizRegion, result.Answer, result.AnswerType, ct);
            if (!clicked)
            {
                RaiseStatus("[경고] 버튼을 찾지 못했습니다.");
            }

            await Task.Delay(500, ct);

            // 다음/제출 버튼 클릭
            var buttonResult = await ClickNextOrSubmitAsync(ct);

            // 답변 미선택 오류 처리 (submit/next 후 test_error.png 감지)
            if (buttonResult == "error")
                return (false, false, true);

            // next 클릭 후에도 오류 재확인 (next에는 엔터가 없으므로 팝업이 남아있음)
            if (buttonResult == "next" && await CheckTestErrorAsync(ct))
                return (false, false, true);

            if (buttonResult == "submit")
            {
                return (true, true, false);
            }
            else if (buttonResult == "next")
            {
                return (true, false, false);
            }
            else
            {
                RaiseStatus("[경고] 다음/제출 버튼을 찾지 못했습니다.");
                return (false, false, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("퀴즈 풀이 오류", ex);
            RaiseStatus($"[오류] {ex.Message}");
            return (false, false, false);
        }
    }

    /// <summary>
    /// 퀴즈 콘텐츠 영역 (사이드바 제외)
    /// </summary>
    private static Rectangle GetQuizContentRegion(Win32Api.RECT windowRect)
    {
        int left = windowRect.Left + SIDEBAR_WIDTH;
        int top = windowRect.Top;
        int width = windowRect.Width - SIDEBAR_WIDTH - 30;
        int height = windowRect.Height;
        return new Rectangle(left, top, width, height);
    }

    /// <summary>
    /// 영역 캡처
    /// </summary>
    private static Mat? CaptureRegion(Rectangle region)
    {
        try
        {
            using var bitmap = new Bitmap(region.Width, region.Height);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.Location, System.Drawing.Point.Empty, region.Size);
            return BitmapConverter.ToMat(bitmap);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 라디오 버튼/체크박스 검출 후 정답 클릭
    /// </summary>
    private async Task<bool> ClickAnswerByDetectionAsync(Mat screenshot, Rectangle quizRegion, string answer, string answerType, CancellationToken ct)
    {
        int scale = Win32Api.GetDisplayScale();

        // 라디오 버튼 또는 체크박스 검출
        string templateName = answerType == "multiple" ? "checkbox.png" : "radio_button.png";
        Logger.Info($"버튼 검출 시작: template={templateName}, 답변={answer}({answerType}), 캡처={screenshot.Width}x{screenshot.Height}");

        var buttons = DetectButtons(screenshot, templateName, scale);

        if (buttons.Count == 0)
        {
            Logger.Warning($"{templateName} 검출 실패 (스크린샷 {screenshot.Width}x{screenshot.Height})");
            return false;
        }

        Logger.Info($"버튼 {buttons.Count}개 검출, 답변={answer}({answerType})");

        // 복수 정답 처리
        if (answerType == "multiple")
        {
            var indices = answer.Split(',')
                .Select(a => int.TryParse(a.Trim(), out int n) ? n - 1 : -1)
                .Where(i => i >= 0 && i < buttons.Count)
                .ToList();

            foreach (var idx in indices)
            {
                var btn = buttons[idx];
                int screenX = quizRegion.X + btn.X;
                int screenY = quizRegion.Y + btn.Y;

                Logger.Info($"체크박스 클릭: {idx + 1}번 at ({screenX}, {screenY})");
                Cursor.Position = new Point(screenX, screenY);
                _inputSimulator.Mouse.LeftButtonClick();
                await Task.Delay(200, ct);
            }

            RaiseStatus($"정답 클릭: {answer}");
            return indices.Count > 0;
        }

        // 단일 정답 처리
        int targetIdx = 0;
        if (answerType == "ox")
        {
            targetIdx = answer == "O" ? 0 : 1;
        }
        else if (int.TryParse(answer, out int num))
        {
            targetIdx = num - 1;
        }

        if (targetIdx >= buttons.Count)
        {
            Logger.Warning($"정답 인덱스 초과: {targetIdx} >= {buttons.Count}");
            return false;
        }

        var button = buttons[targetIdx];
        int clickX = quizRegion.X + button.X;
        int clickY = quizRegion.Y + button.Y;

        Logger.Info($"정답 클릭: {answer} at ({clickX}, {clickY})");
        Cursor.Position = new Point(clickX, clickY);
        _inputSimulator.Mouse.LeftButtonClick();
        RaiseStatus($"정답 클릭: {answer}");

        return true;
    }

    /// <summary>
    /// 버튼 템플릿 매칭으로 위치 검출
    /// </summary>
    private List<Point> DetectButtons(Mat screenshot, string templateName, int scale)
    {
        var result = new List<Point>();

        try
        {
            string templatePath = Path.Combine(Config.RefFolder, scale.ToString(), templateName);
            // 배율별 템플릿 없으면 100% 폴백
            if (!File.Exists(templatePath))
                templatePath = Path.Combine(Config.RefFolder, "100", templateName);
            if (!File.Exists(templatePath))
            {
                Logger.Warning($"템플릿 없음: {templatePath}");
                return result;
            }

            // 한글 경로 안전 로딩 (ImageMatcher.ImReadSafe 사용)
            using var template = ImageMatcher.ImReadSafe(templatePath);
            if (template == null || template.Empty()) return result;

            using var grayScreen = new Mat();
            using var grayTemplate = new Mat();
            Cv2.CvtColor(screenshot, grayScreen, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(template, grayTemplate, ColorConversionCodes.BGR2GRAY);

            // 여러 스케일로 매칭
            var allMatches = new List<(int X, int Y, double Score)>();
            double[] scales = { 0.8, 0.9, 1.0, 1.1, 1.2 };
            double bestScoreOverall = 0;

            foreach (var s in scales)
            {
                int newW = (int)(grayTemplate.Width * s);
                int newH = (int)(grayTemplate.Height * s);
                if (newW < 5 || newH < 5) continue;

                using var resized = new Mat();
                Cv2.Resize(grayTemplate, resized, new OpenCvSharp.Size(newW, newH));

                using var matchResult = new Mat();
                Cv2.MatchTemplate(grayScreen, resized, matchResult, TemplateMatchModes.CCoeffNormed);

                matchResult.MinMaxLoc(out _, out double maxVal, out _, out _);
                if (maxVal > bestScoreOverall) bestScoreOverall = maxVal;

                for (int y = 0; y < matchResult.Rows; y++)
                {
                    for (int x = 0; x < matchResult.Cols; x++)
                    {
                        float score = matchResult.At<float>(y, x);
                        if (score >= BUTTON_CONFIDENCE)
                        {
                            int cx = x + newW / 2;
                            int cy = y + newH / 2;
                            allMatches.Add((cx, cy, score));
                        }
                    }
                }
            }

            Logger.Info($"{templateName} 매칭: threshold={BUTTON_CONFIDENCE}, bestScore={bestScoreOverall:F3}, matches={allMatches.Count}");

            // NMS: 가까운 검출 중 가장 높은 점수만
            allMatches = allMatches.OrderByDescending(m => m.Score).ToList();
            var kept = new List<(int X, int Y, double Score)>();

            foreach (var m in allMatches)
            {
                bool tooClose = kept.Any(k => Math.Abs(m.X - k.X) < 25 && Math.Abs(m.Y - k.Y) < 25);
                if (!tooClose)
                {
                    kept.Add(m);
                }
            }

            // X좌표 클러스터링: 라디오 버튼은 같은 X컬럼에 정렬됨
            // 다른 X위치의 false positive 제거
            if (kept.Count >= 3)
            {
                var sortedByX = kept.OrderBy(m => m.X).ToList();
                int medianX = sortedByX[sortedByX.Count / 2].X;
                var filtered = kept.Where(m => Math.Abs(m.X - medianX) < 50).ToList();
                if (filtered.Count >= 2 && filtered.Count < kept.Count)
                {
                    Logger.Info($"X-필터링: {kept.Count}개 → {filtered.Count}개 (medianX={medianX})");
                    kept = filtered;
                }
            }

            // Y좌표 정렬
            var keptSorted = kept.OrderBy(m => m.Y).ToList();
            result = keptSorted.Select(m => new Point(m.X, m.Y)).ToList();

            // 상세 로깅: 각 검출 항목의 신뢰도 점수 포함
            Logger.Info($"{templateName} {result.Count}개 검출 (NMS 전 {allMatches.Count}개)");
            for (int bi = 0; bi < keptSorted.Count; bi++)
            {
                var m = keptSorted[bi];
                Logger.Info($"  버튼[{bi}]: ({m.X}, {m.Y}) score={m.Score:F3}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"버튼 검출 오류: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 다음/제출 버튼 클릭
    /// </summary>
    private async Task<string?> ClickNextOrSubmitAsync(CancellationToken ct)
    {
        int scale = Win32Api.GetDisplayScale();

        // 전체 화면 캡처 + 그레이 1회 변환
        using var screen = ScreenCapture.CaptureScreen();
        using var grayScreen = new Mat();
        Cv2.CvtColor(screen, grayScreen, ColorConversionCodes.BGR2GRAY);

        // 다음 버튼과 제출 버튼 찾기
        var nextResult = _imageMatcher.FindTemplateWithScoreFromGray(grayScreen, "next_button.png", scale, 0.85);
        var submitResult = _imageMatcher.FindTemplateWithScoreFromGray(grayScreen, "submit.png", scale, 0.85);

        Logger.Debug($"next 신뢰도: {nextResult.Score:F3}, submit 신뢰도: {submitResult.Score:F3}");

        // 둘 다 있으면 신뢰도 비교
        if (nextResult.Result.Found && submitResult.Result.Found)
        {
            if (submitResult.Score > nextResult.Score)
            {
                if (!await ClickSubmitAsync(submitResult.Result.Center, ct)) return "error";
                return "submit";
            }
            else
            {
                await ClickButtonAsync(nextResult.Result.Center, "다음", ct);
                return "next";
            }
        }

        if (submitResult.Result.Found)
        {
            await ClickSubmitAsync(submitResult.Result.Center, ct);
            return "submit";
        }

        if (nextResult.Result.Found)
        {
            await ClickButtonAsync(nextResult.Result.Center, "다음", ct);
            return "next";
        }

        // 못 찾으면 PageDown 후 재시도
        _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.NEXT);
        await Task.Delay(500, ct);

        using var screen2 = ScreenCapture.CaptureScreen();
        using var grayScreen2 = new Mat();
        Cv2.CvtColor(screen2, grayScreen2, ColorConversionCodes.BGR2GRAY);
        var nextResult2 = _imageMatcher.FindTemplateWithScoreFromGray(grayScreen2, "next_button.png", scale, 0.85);
        var submitResult2 = _imageMatcher.FindTemplateWithScoreFromGray(grayScreen2, "submit.png", scale, 0.85);

        if (submitResult2.Result.Found)
        {
            if (!await ClickSubmitAsync(submitResult2.Result.Center, ct)) return "error";
            return "submit";
        }

        if (nextResult2.Result.Found)
        {
            await ClickButtonAsync(nextResult2.Result.Center, "다음", ct);
            return "next";
        }

        return null;
    }

    private async Task ClickButtonAsync(Point center, string name, CancellationToken ct)
    {
        Logger.Info($"{name} 버튼 클릭: ({center.X}, {center.Y})");
        Cursor.Position = center;
        await Task.Delay(100, ct);
        _inputSimulator.Mouse.LeftButtonClick();
        RaiseStatus($"{name} 문제로 이동");
    }

    /// <returns>true: 정상 제출, false: 답변 미선택 오류</returns>
    private async Task<bool> ClickSubmitAsync(Point center, CancellationToken ct)
    {
        Logger.Info($"제출 버튼 클릭: ({center.X}, {center.Y})");
        Cursor.Position = center;
        await Task.Delay(100, ct);
        _inputSimulator.Mouse.LeftButtonClick();
        RaiseStatus("답안 제출 중...");
        await Task.Delay(500, ct);

        // 답변 미선택 오류면 엔터로 팝업 닫고 재시도 필요
        if (await CheckTestErrorAsync(ct))
            return false;

        _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
        RaiseStatus("답안 제출 완료!");
        return true;
    }

    /// <summary>
    /// 답변 미선택 오류 팝업 확인 및 닫기 (test_error.png)
    /// </summary>
    private async Task<bool> CheckTestErrorAsync(CancellationToken ct)
    {
        await Task.Delay(300, ct);
        int scale = Win32Api.GetDisplayScale();
        using var screen = ScreenCapture.CaptureScreen();
        var errorCheck = _imageMatcher.FindTemplateWithScore(screen, "test_error.png", scale, 0.7);
        if (errorCheck.Result.Found)
        {
            Logger.Warning("답변 미선택 오류 감지 (test_error.png) - 엔터로 닫기");
            RaiseStatus("답변 오류 감지 - 재시도");
            _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
            await Task.Delay(300, ct);
            return true;
        }
        return false;
    }

    private void RaiseStatus(string message)
    {
        OnStatusChanged?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        if (_ownsImageMatcher) _imageMatcher.Dispose();
        _cts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
