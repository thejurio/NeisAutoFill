namespace DooClick;

/// <summary>
/// 로깅 서비스 (StreamWriter 버퍼링으로 I/O 최적화, 날짜 롤오버 지원)
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string? _logFilePath;
    private static StreamWriter? _writer;
    private static string _currentDate = "";

    // 로그 파일 최대 크기 (10MB)
    private const long MaxLogFileSize = 10 * 1024 * 1024;

    public static string LogFolder => Path.Combine(Config.ProjectRoot, "logs");

    /// <summary>
    /// 로그 초기화
    /// </summary>
    public static void Initialize()
    {
        try
        {
            if (!Directory.Exists(LogFolder))
                Directory.CreateDirectory(LogFolder);

            OpenLogFile();
        }
        catch
        {
            // 로그 폴더 생성 실패 시 무시
        }
    }

    /// <summary>
    /// 로그 파일 열기 (현재 날짜 기준)
    /// </summary>
    private static void OpenLogFile()
    {
        _currentDate = DateTime.Now.ToString("yyyy-MM-dd");
        _logFilePath = Path.Combine(LogFolder, $"{_currentDate}.log");

        _writer = new StreamWriter(_logFilePath, append: true)
        {
            AutoFlush = true
        };
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Debug(string message) => Log("DEBUG", message);
    public static void Warning(string message) => Log("WARN", message);
    public static void Error(string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message}: {ex.Message}" : message;
        Log("ERROR", msg);
    }

    private static void Log(string level, string message)
    {
        var now = DateTime.Now;
        var timestamp = now.ToString("HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        // 콘솔 출력
        Console.WriteLine(line);

        // 파일 출력 (StreamWriter 재사용)
        try
        {
            lock (_lock)
            {
                // 날짜 변경 시 로그 파일 롤오버
                var today = now.ToString("yyyy-MM-dd");
                if (today != _currentDate)
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                    OpenLogFile();
                }

                _writer?.WriteLine(line);
            }
        }
        catch
        {
            // 파일 쓰기 실패 시 무시
        }
    }

    /// <summary>
    /// 앱 종료 시 DEBUG/INFO 제거 후 리소스 정리
    /// </summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            CleanupLogFile();
        }
    }

    /// <summary>
    /// 로그 파일에서 DEBUG/INFO 제거, 10MB 초과 시 오래된 기록 삭제
    /// </summary>
    private static void CleanupLogFile()
    {
        try
        {
            if (_logFilePath == null || !File.Exists(_logFilePath)) return;

            var lines = File.ReadAllLines(_logFilePath);
            // WARN/ERROR만 남기기
            var filtered = lines.Where(line =>
                line.Contains("] [WARN]") || line.Contains("] [ERROR]")
            ).ToArray();

            File.WriteAllLines(_logFilePath, filtered);

            // 10MB 초과 시 앞쪽(오래된 기록) 삭제
            var fileInfo = new FileInfo(_logFilePath);
            if (fileInfo.Length > MaxLogFileSize)
            {
                int removeCount = filtered.Length * 30 / 100;
                if (removeCount < 100) removeCount = Math.Min(100, filtered.Length / 2);
                var trimmed = filtered.Skip(removeCount).ToArray();
                File.WriteAllLines(_logFilePath, trimmed);
            }
        }
        catch
        {
            // 정리 실패 시 무시 (로그 파일이 남아있는 게 낫다)
        }
    }
}
