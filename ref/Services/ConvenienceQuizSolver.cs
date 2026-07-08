using System.Drawing;
using DooClick.Core;
using DooClick.Utils;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using WindowsInput;
using Point = System.Drawing.Point;

namespace DooClick.Services;

/// <summary>
/// 편리모드 전용 AI 퀴즈 풀이
/// QuizSolver와 동일한 풀이 로직이지만 모든 I/O를 편리모드 API로 교체:
/// - 캡처: ScreenCapture.CaptureWindow (PrintWindow)
/// - 클릭: AutomationHelper.ClickAt (PostMessage)
/// - 키 입력: ConvenienceMode.PostKey (PostMessage)
/// </summary>
public class ConvenienceQuizSolver : IDisposable
{
    private readonly ImageMatcher _imageMatcher;
    private readonly IntPtr _hwnd;
    private readonly GeminiSolver _geminiSolver;
    private readonly InputSimulator _inputSimulator;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private const double BUTTON_CONFIDENCE = 0.85;
    private static readonly int SIDEBAR_WIDTH = Config.SidebarWidth;

    public event Action<string>? OnStatusChanged;
    public event Action<bool>? OnCompleted;

    public bool IsRunning { get; private set; }

    /// <summary>
    /// 편리모드 퀴즈 풀이 생성자
    /// </summary>
    /// <param name="sharedImageMatcher">공유 ImageMatcher (템플릿 캐시 공유)</param>
    /// <param name="quizHwnd">퀴즈 창 핸들 (편리모드 캡처/클릭 대상)</param>
    public ConvenienceQuizSolver(ImageMatcher sharedImageMatcher, IntPtr quizHwnd)
    {
        _imageMatcher = sharedImageMatcher;
        _hwnd = quizHwnd;
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
                Logger.Error("편리모드 퀴즈 풀이 오류", ex);
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
            Logger.Info($"[편리모드] 문제 {i + 1} 풀이 시작");

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
                Logger.Info($"[편리모드] 문제 {i + 1} 풀이 성공");

                if (isComplete)
                {
                    RaiseStatus($"퀴즈 완료! {solvedCount}문제 풀이");
                    Logger.Info($"[편리모드] 퀴즈 완료! 총 {solvedCount}문제");
                    break;
                }
            }
            else
            {
                // API 실패 등으로 답을 못 골랐으면 다음 버튼 누르지 않고 재시도
                Logger.Warning($"[편리모드] 문제 {i + 1} 풀이 실패 - 다음 버튼 안 누르고 재시도");

                // 혹시 test_error 팝업이 떠있으면 엔터로 닫기
                if (await CheckTestErrorAsync(ct))
                {
                    Logger.Warning("[편리모드] test_error 팝업 감지 → 엔터로 닫음");
                }

                if (retryCount < maxRetries)
                {
                    retryCount++;
                    i--;
                    await Task.Delay(1000, ct);
                    continue;
                }
                // 재시도 한도 초과 시 다음 문제로 넘기기
                Logger.Warning($"[편리모드] 문제 {i + 1} 재시도 한도 초과 - 스킵");
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
            if (!Config.ValidateApiKey())
            {
                RaiseStatus("[오류] API 키가 설정되지 않았습니다.");
                return (false, false, false);
            }

            if (_hwnd == IntPtr.Zero)
            {
                RaiseStatus("퀴즈 창 핸들이 유효하지 않습니다.");
                return (false, false, false);
            }

            // PageDown으로 문제 전체 보이게 (PostMessage)
            ConvenienceMode.PostKey(_hwnd, Win32Api.VK_NEXT);
            await Task.Delay(150, ct);

            // 창 캡처 (PrintWindow - 사이드바 크롭 불필요)
            RaiseStatus("퀴즈 화면 캡처 중...");
            using var screenshot = ScreenCapture.CaptureWindow(_hwnd);
            if (screenshot == null || screenshot.Empty())
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

            Logger.Info($"[편리모드] AI 응답: {result.Answer} (유형: {result.AnswerType})");
            RaiseStatus($"AI 정답: {result.Answer}");

            await Task.Delay(500, ct);

            // 라디오 버튼/체크박스 검출 및 클릭
            bool clicked = await ClickAnswerByDetectionAsync(screenshot, result.Answer, result.AnswerType, ct);
            if (!clicked)
            {
                RaiseStatus("[경고] 버튼을 찾지 못했습니다.");
            }

            await Task.Delay(500, ct);

            // 다음/제출 버튼 클릭
            var buttonResult = await ClickNextOrSubmitAsync(ct);

            // 답변 미선택 오류 처리
            if (buttonResult == "error")
                return (false, false, true);

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
            Logger.Error("편리모드 퀴즈 풀이 오류", ex);
            RaiseStatus($"[오류] {ex.Message}");
            return (false, false, false);
        }
    }

    /// <summary>
    /// 라디오 버튼/체크박스 검출 후 정답 클릭
    /// 캡처 이미지 좌표 → AutomationHelper.ClickAt (WindowToClient 자동 변환)
    /// </summary>
    private async Task<bool> ClickAnswerByDetectionAsync(Mat screenshot, string answer, string answerType, CancellationToken ct)
    {
        int scale = Win32Api.GetDisplayScale();

        string templateName = answerType == "multiple" ? "checkbox.png" : "radio_button.png";

        // 진단 로깅: 창 상태 확인
        Win32Api.GetWindowRect(_hwnd, out var wRect);
        Logger.Info($"[편리모드] 퀴즈 창 상태: hwnd={_hwnd}, pos=({wRect.Left},{wRect.Top}), size=({wRect.Width}x{wRect.Height}), 캡처={screenshot.Width}x{screenshot.Height}");

        var buttons = DetectButtons(screenshot, templateName, scale);

        if (buttons.Count == 0)
        {
            Logger.Warning($"[편리모드] {templateName} 검출 실패 (스크린샷 {screenshot.Width}x{screenshot.Height})");
            return false;
        }

        Logger.Info($"[편리모드] 버튼 {buttons.Count}개 검출, 답변={answer}({answerType})");

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
                Logger.Info($"[편리모드] 체크박스 클릭: {idx + 1}번 at ({btn.X}, {btn.Y})");
                AutomationHelper.ClickAt(btn, _hwnd, true, _inputSimulator);
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
            Logger.Warning($"[편리모드] 정답 인덱스 초과: {targetIdx} >= {buttons.Count}");
            return false;
        }

        var button = buttons[targetIdx];
        // 진단: 클릭 전 좌표 변환 확인
        var clientPos = ConvenienceMode.WindowToClient(_hwnd, button.X, button.Y);
        Logger.Info($"[편리모드] 정답 클릭: {answer}번 → buttons[{targetIdx}] at image({button.X},{button.Y}) → client({clientPos.X},{clientPos.Y})");
        AutomationHelper.ClickAt(button, _hwnd, true, _inputSimulator);
        RaiseStatus($"정답 클릭: {answer}");

        return true;
    }

    /// <summary>
    /// 버튼 템플릿 매칭으로 위치 검출 (QuizSolver와 동일 로직)
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

            Logger.Info($"[편리모드] {templateName} 매칭: threshold={BUTTON_CONFIDENCE}, bestScore={bestScoreOverall:F3}, matches={allMatches.Count}");

            // 사이드바 영역 제외 (편리모드는 전체 창 캡처이므로 사이드바 포함됨)
            allMatches = allMatches.Where(m => m.X > SIDEBAR_WIDTH).ToList();

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
            // 다른 X위치의 false positive 제거 (사이드바 요소, 문제번호 배지 등)
            if (kept.Count >= 3)
            {
                var sortedByX = kept.OrderBy(m => m.X).ToList();
                int medianX = sortedByX[sortedByX.Count / 2].X;
                var filtered = kept.Where(m => Math.Abs(m.X - medianX) < 50).ToList();
                if (filtered.Count >= 2 && filtered.Count < kept.Count)
                {
                    Logger.Info($"[편리모드] X-필터링: {kept.Count}개 → {filtered.Count}개 (medianX={medianX})");
                    kept = filtered;
                }
            }

            var keptSorted = kept.OrderBy(m => m.Y).ToList();
            result = keptSorted.Select(m => new Point(m.X, m.Y)).ToList();

            // 상세 로깅: 각 검출 항목의 신뢰도 점수 포함
            Logger.Info($"[편리모드] {templateName} {result.Count}개 검출 (NMS 전 {allMatches.Count}개)");
            for (int bi = 0; bi < keptSorted.Count; bi++)
            {
                var m = keptSorted[bi];
                Logger.Info($"[편리모드]   버튼[{bi}]: ({m.X}, {m.Y}) score={m.Score:F3}");
            }

            // 디버그 시각화: 검출 결과를 스크린샷에 그려서 PNG 저장
            try
            {
                using var debugImg = screenshot.Clone();
                int tw = grayTemplate.Width / 2;
                int th = grayTemplate.Height / 2;

                for (int bi = 0; bi < keptSorted.Count; bi++)
                {
                    var m = keptSorted[bi];
                    var color = m.Score >= 0.85 ? new Scalar(0, 255, 0) : new Scalar(0, 165, 255); // 초록=높음, 주황=낮음
                    Cv2.Rectangle(debugImg,
                        new OpenCvSharp.Point(m.X - tw, m.Y - th),
                        new OpenCvSharp.Point(m.X + tw, m.Y + th),
                        color, 2);
                    Cv2.PutText(debugImg, $"[{bi}] {m.Score:F2}",
                        new OpenCvSharp.Point(m.X + tw + 3, m.Y + 5),
                        HersheyFonts.HersheySimplex, 0.5, color, 1);
                }

                // 사이드바 경계선 (빨간 점선)
                Cv2.Line(debugImg, new OpenCvSharp.Point(SIDEBAR_WIDTH, 0),
                    new OpenCvSharp.Point(SIDEBAR_WIDTH, debugImg.Height), new Scalar(0, 0, 255), 1);

                var debugPath = Path.Combine(Logger.LogFolder, "debug_detection.png");
                Cv2.ImWrite(debugPath, debugImg);
                Logger.Info($"[편리모드] 디버그 이미지 저장: {debugPath}");
            }
            catch (Exception debugEx)
            {
                Logger.Warning($"[편리모드] 디버그 이미지 저장 실패: {debugEx.Message}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[편리모드] 버튼 검출 오류: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 다음/제출 버튼 클릭 (창 캡처 + PostMessage 클릭)
    /// </summary>
    private async Task<string?> ClickNextOrSubmitAsync(CancellationToken ct)
    {
        int scale = Win32Api.GetDisplayScale();

        // 창 캡처 + 그레이 변환
        using var screen = AutomationHelper.CaptureScreen(_hwnd, true);
        using var grayScreen = new Mat();
        Cv2.CvtColor(screen, grayScreen, ColorConversionCodes.BGR2GRAY);

        var nextResult = _imageMatcher.FindTemplateWithScoreFromGray(grayScreen, "next_button.png", scale, 0.85);
        var submitResult = _imageMatcher.FindTemplateWithScoreFromGray(grayScreen, "submit.png", scale, 0.85);

        Logger.Debug($"[편리모드] next 신뢰도: {nextResult.Score:F3}, submit 신뢰도: {submitResult.Score:F3}");

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
            if (!await ClickSubmitAsync(submitResult.Result.Center, ct)) return "error";
            return "submit";
        }

        if (nextResult.Result.Found)
        {
            await ClickButtonAsync(nextResult.Result.Center, "다음", ct);
            return "next";
        }

        // 못 찾으면 PageDown 후 재시도
        ConvenienceMode.PostKey(_hwnd, Win32Api.VK_NEXT);
        await Task.Delay(500, ct);

        using var screen2 = AutomationHelper.CaptureScreen(_hwnd, true);
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
        Logger.Info($"[편리모드] {name} 버튼 클릭: ({center.X}, {center.Y})");
        AutomationHelper.ClickAt(center, _hwnd, true, _inputSimulator);
        await Task.Delay(100, ct);
        RaiseStatus($"{name} 문제로 이동");
    }

    /// <returns>true: 정상 제출, false: 답변 미선택 오류</returns>
    private async Task<bool> ClickSubmitAsync(Point center, CancellationToken ct)
    {
        Logger.Info($"[편리모드] 제출 버튼 클릭: ({center.X}, {center.Y})");
        AutomationHelper.ClickAt(center, _hwnd, true, _inputSimulator);
        RaiseStatus("답안 제출 중...");
        await Task.Delay(500, ct);

        // 답변 미선택 오류면 엔터로 팝업 닫고 재시도 필요
        if (await CheckTestErrorAsync(ct))
            return false;

        ConvenienceMode.PostKey(_hwnd, Win32Api.VK_RETURN);
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
        using var screen = AutomationHelper.CaptureScreen(_hwnd, true);
        var errorCheck = _imageMatcher.FindTemplateWithScore(screen, "test_error.png", scale, 0.7);
        if (errorCheck.Result.Found)
        {
            Logger.Warning("[편리모드] 답변 미선택 오류 감지 - 엔터로 닫기");
            RaiseStatus("답변 오류 감지 - 재시도");
            ConvenienceMode.PostKey(_hwnd, Win32Api.VK_RETURN);
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
        _cts?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
