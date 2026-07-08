using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DooClick.Services;

/// <summary>
/// Gemini API 기반 퀴즈 풀이
/// </summary>
public class GeminiSolver
{
    private readonly HttpClient _httpClient;
    private const int MaxRetries = 3;
    private const int RetryDelayMs = 2000;

    public GeminiSolver()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// 이미지에서 퀴즈 정답 추출
    /// </summary>
    /// <summary>
    /// 모든 API 키가 소진되었을 때 발생하는 에러 코드
    /// </summary>
    public const int ERROR_ALL_KEYS_EXHAUSTED = 4290;

    public async Task<QuizResult> SolveQuizFromImageAsync(byte[] imageBytes)
    {
        var base64Image = Convert.ToBase64String(imageBytes);
        var prompt = CreatePrompt();

        var requestBody = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = prompt },
                        new
                        {
                            inline_data = new
                            {
                                mime_type = "image/png",
                                data = base64Image
                            }
                        }
                    }
                }
            }
        };

        const int maxKeyChanges = 20; // 서버 키 최대 교체 횟수
        int keyChangeCount = 0;
        int httpRetryCount = 0;

        while (true)
        {
            // 매 시도마다 키 새로 조회 (429 후 키 교체 반영)
            var apiKey = await Config.GetApiKeyAsync();
            if (string.IsNullOrEmpty(apiKey))
            {
                Logger.Warning("사용 가능한 API 키 없음 - 모든 키 소진");
                return new QuizResult
                {
                    Success = false,
                    Error = "오늘 사용 가능한 API 사용량을 모두 소진했습니다.\n내일 다시 시도해주세요.",
                    ErrorCode = ERROR_ALL_KEYS_EXHAUSTED
                };
            }

            var url = $"{Config.GeminiApiUrl}?key={apiKey}";

            try
            {
                var response = await _httpClient.PostAsJsonAsync(url, requestBody);

                // 429: 현재 키 만료 → 다른 키로 교체
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    keyChangeCount++;
                    Logger.Warning($"API 429 - 키 만료 처리 후 교체 ({keyChangeCount}/{maxKeyChanges})");
                    Core.ApiKeyManager.Instance.MarkKeyExhausted();

                    if (keyChangeCount >= maxKeyChanges)
                    {
                        return new QuizResult
                        {
                            Success = false,
                            Error = "오늘 사용 가능한 API 사용량을 모두 소진했습니다.\n내일 다시 시도해주세요.",
                            ErrorCode = ERROR_ALL_KEYS_EXHAUSTED
                        };
                    }

                    await Task.Delay(1000); // 키 교체 후 잠시 대기
                    continue;
                }

                // 다른 HTTP 에러 (3회 재시도)
                if (!response.IsSuccessStatusCode)
                {
                    httpRetryCount++;
                    var errorBody = await response.Content.ReadAsStringAsync();
                    var snippet = errorBody.Length > 200 ? errorBody[..200] : errorBody;
                    Logger.Warning($"API HTTP {(int)response.StatusCode} (시도 {httpRetryCount}/{MaxRetries}): {snippet}");

                    if (httpRetryCount < MaxRetries)
                    {
                        await Task.Delay(RetryDelayMs * httpRetryCount);
                        continue;
                    }
                    return new QuizResult
                    {
                        Success = false,
                        Error = $"API 오류 (HTTP {(int)response.StatusCode})"
                    };
                }

                var json = await response.Content.ReadAsStringAsync();
                var result = ParseResponse(json);
                return result;
            }
            catch (HttpRequestException ex)
            {
                httpRetryCount++;
                Logger.Warning($"API 호출 실패 (시도 {httpRetryCount}/{MaxRetries}): {ex.Message}");
                if (httpRetryCount >= MaxRetries)
                    return new QuizResult { Success = false, Error = $"API 연결 실패: {ex.Message}" };
                await Task.Delay(RetryDelayMs * httpRetryCount);
            }
            catch (TaskCanceledException)
            {
                httpRetryCount++;
                Logger.Warning($"API 타임아웃 (시도 {httpRetryCount}/{MaxRetries})");
                if (httpRetryCount >= MaxRetries)
                    return new QuizResult { Success = false, Error = "API 응답 타임아웃" };
                await Task.Delay(RetryDelayMs * httpRetryCount);
            }
            catch (Exception ex)
            {
                return new QuizResult
                {
                    Success = false,
                    Error = $"API 오류: {ex.Message}"
                };
            }
        }
    }

    private static string CreatePrompt()
    {
        return """
            이 이미지는 교원연수 퀴즈 화면입니다.

            문제를 분석하고 다음 형식으로 정확히 답해주세요:

            [응답 형식]
            정답: (O 또는 X 또는 번호)
            유형: (single 또는 multiple)

            [예시 - OX문제]
            정답: X
            유형: single

            [예시 - 객관식 단일 정답]
            정답: 2
            유형: single

            [예시 - 객관식 복수 정답 (모두 고르시오)]
            정답: 1,3,4
            유형: multiple

            중요:
            - "모두 고르시오", "모두 선택" 등의 문구가 있으면 복수 정답(multiple)입니다.
            - 복수 정답인 경우 정답 번호를 쉼표로 구분하세요.
            """;
    }

    private static QuizResult ParseResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";

            // 정답 파싱
            var answerMatch = Regex.Match(text, @"정답[:\s]*([OXox0-9,\s]+)", RegexOptions.IgnoreCase);
            var typeMatch = Regex.Match(text, @"유형[:\s]*(single|multiple)", RegexOptions.IgnoreCase);

            string answer;
            string answerType;

            if (!answerMatch.Success)
            {
                // 정답: 형식이 없으면 직접 파싱
                if (text.Contains("O") && !text.Contains("X"))
                {
                    answer = "O";
                    answerType = "ox";
                }
                else if (text.Contains("X"))
                {
                    answer = "X";
                    answerType = "ox";
                }
                else
                {
                    var numMatch = Regex.Match(text, @"(\d+)");
                    if (numMatch.Success)
                    {
                        answer = numMatch.Groups[1].Value;
                        answerType = "number";
                    }
                    else
                    {
                        return new QuizResult
                        {
                            Success = false,
                            Error = "정답을 파싱할 수 없습니다.",
                            RawResponse = text
                        };
                    }
                }
            }
            else
            {
                var rawAnswer = answerMatch.Groups[1].Value.ToUpper().Trim();
                var isMultiple = typeMatch.Success && typeMatch.Groups[1].Value.ToLower() == "multiple";

                if (rawAnswer is "O" or "X")
                {
                    answer = rawAnswer;
                    answerType = "ox";
                }
                else if (rawAnswer.Contains(',') || isMultiple)
                {
                    // 복수 정답
                    var answers = rawAnswer.Replace(" ", "").Split(',')
                        .Where(a => int.TryParse(a, out _))
                        .ToArray();
                    answer = string.Join(",", answers);
                    answerType = "multiple";
                }
                else
                {
                    answer = rawAnswer;
                    answerType = "number";
                }
            }

            return new QuizResult
            {
                Success = true,
                Answer = answer,
                AnswerType = answerType,
                RawResponse = text
            };
        }
        catch (Exception ex)
        {
            return new QuizResult
            {
                Success = false,
                Error = $"응답 파싱 실패: {ex.Message}"
            };
        }
    }
}

/// <summary>
/// 퀴즈 풀이 결과
/// </summary>
public class QuizResult
{
    public bool Success { get; init; }
    public string Answer { get; init; } = string.Empty;
    public string AnswerType { get; init; } = string.Empty; // ox, number, multiple
    public string? Error { get; init; }
    public int? ErrorCode { get; init; }
    public string? RawResponse { get; init; }
}
