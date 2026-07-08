using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WindowsInput;
using WindowsInput.Native;
using DooClick.Core;
using DooClick.Utils;
using OpenCvSharp;

namespace DooClick.Services;

/// <summary>
/// 로그인 서비스 (Chrome 실행, 로그인 처리)
/// </summary>
public class LoginService
{
    private readonly ImageMatcher _imageMatcher;
    private readonly InputSimulator _inputSimulator;
    private readonly string _portalUrl;
    private readonly HiddenWindowsManager _windowsManager = HiddenWindowsManager.Instance;

    private IntPtr _browserHwnd;
    private bool _convenienceMode;

    public event Action<string>? OnStatusChanged;

    public IntPtr BrowserHwnd => _browserHwnd;

    public LoginService(ImageMatcher imageMatcher, string portalUrl)
    {
        _imageMatcher = imageMatcher;
        _inputSimulator = new InputSimulator();
        _portalUrl = portalUrl;
    }

    private void RaiseStatus(string message)
    {
        OnStatusChanged?.Invoke(message);
        Logger.Info($"[로그인] {message}");
    }

    /// <summary>
    /// Chrome 실행 및 로그인 전체 프로세스
    /// </summary>
    public async Task<bool> LaunchAndLoginAsync(string username, string password, int scale, bool convenienceMode, CancellationToken ct)
    {
        _convenienceMode = convenienceMode;

        // 1. Chrome 실행
        RaiseStatus("Chrome 실행 중...");
        if (!LaunchChromeWithFlags(convenienceMode))
        {
            RaiseStatus("[오류] Chrome 실행 실패");
            return false;
        }

        await Task.Delay(2000, ct);

        // 2. 브라우저 창 찾기
        _browserHwnd = AutomationHelper.FindBrowserWindow();
        if (_browserHwnd == IntPtr.Zero)
        {
            Logger.Warning("브라우저 창을 찾지 못함");
        }
        else
        {
            Logger.Info($"브라우저 창 발견: hwnd={_browserHwnd}");

            // 창이 완전히 로딩될 때까지 대기
            await Task.Delay(2000, ct);

            if (convenienceMode)
            {
                // 화면 밖에서 창 크기를 화면 크기로 강제 조정 후 숨김 등록
                var screen = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                Win32Api.SetWindowPos(_browserHwnd, IntPtr.Zero,
                    Config.OffScreenX, Config.OffScreenY, screen.Width, screen.Height, Win32Api.SWP_NOZORDER);
                Logger.Info($"편리모드: 창 크기 조정 ({screen.Width}x{screen.Height}), 숨김 처리");
                _windowsManager.HideWindow(_browserHwnd);
                RaiseStatus("편리모드: 브라우저 창 숨김");
            }
        }

        // 3. 로그인
        RaiseStatus("로그인 진행 중...");
        if (!await LoginAsync(username, password, scale, ct))
        {
            RaiseStatus("[오류] 로그인 실패");
            return false;
        }

        RaiseStatus("로그인 성공!");
        return true;
    }

    /// <summary>
    /// 이미 열린 브라우저에서 로그인만 수행
    /// </summary>
    public async Task<bool> LoginAsync(string username, string password, int scale, CancellationToken ct)
    {
        Logger.Info("로그인 시도 시작");

        // 팝업 닫기
        await AutomationHelper.ClosePopupsAsync(_imageMatcher, scale, _browserHwnd, _convenienceMode, _inputSimulator, ct);

        // 이미 로그인 상태인지 확인
        using (var checkScreen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode))
        {
            using var grayCheck = new Mat();
            Cv2.CvtColor(checkScreen, grayCheck, ColorConversionCodes.BGR2GRAY);
            var logout = _imageMatcher.FindTemplateFromGray(grayCheck, "logout.png", scale, 0.85);

            // lectures.png는 비로그인 상태에서도 표시될 수 있으므로 logout 버튼으로만 판단
            if (logout.Found)
            {
                Logger.Info("이미 로그인 상태 확인됨 (logout 버튼 감지)");
                RaiseStatus("이미 로그인되어 있습니다.");
                return true;
            }
        }

        // 로그인 버튼 찾기
        for (int i = 0; i < 20; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode);
            var loginBtn = _imageMatcher.FindTemplate(screen, "login_btn.png", scale, 0.8);

            if (loginBtn.Found)
            {
                RaiseStatus("로그인 버튼 클릭...");
                AutomationHelper.ClickAt(loginBtn.Center, _browserHwnd, _convenienceMode, _inputSimulator);
                await Task.Delay(1500, ct);

                // 편리모드: 페이지 이동 후 창이 최소화될 수 있으므로 크기 복원
                if (_convenienceMode)
                {
                    Win32Api.GetWindowRect(_browserHwnd, out var rect);
                    if (rect.Width < 500 || rect.Height < 500)
                    {
                        var screenArea = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
                        Win32Api.ShowWindow(_browserHwnd, 9); // SW_RESTORE
                        await Task.Delay(300, ct);
                        Win32Api.SetWindowPos(_browserHwnd, IntPtr.Zero,
                            Config.OffScreenX, Config.OffScreenY, screenArea.Width, screenArea.Height, Win32Api.SWP_NOZORDER);
                        Logger.Info($"로그인 후 창 크기 복원: {rect.Width}x{rect.Height} → {screenArea.Width}x{screenArea.Height}");
                    }
                }
                break;
            }

            await Task.Delay(500, ct);
        }

        // ID 입력 필드 찾기 및 로그인
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var screen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode);
            var idField = _imageMatcher.FindTemplate(screen, "login_id_field.png", scale, 0.85);

            if (idField.Found)
            {
                RaiseStatus("아이디 입력 중...");

                AutomationHelper.ClickAt(idField.Center, _browserHwnd, _convenienceMode, _inputSimulator);
                await Task.Delay(300, ct);

                if (_convenienceMode)
                {
                    // 편리모드: PostMessage로 텍스트 입력
                    ConvenienceMode.PostTextWithClear(_browserHwnd, username);
                    await Task.Delay(500, ct);

                    ConvenienceMode.PostKey(_browserHwnd, Win32Api.VK_TAB);
                    await Task.Delay(500, ct);
                    // 비밀번호도 기존 텍스트 삭제 후 입력 (자동완성 대비)
                    ConvenienceMode.PostTextWithClear(_browserHwnd, password);
                    await Task.Delay(300, ct);

                    ConvenienceMode.PostKey(_browserHwnd, Win32Api.VK_RETURN);
                }
                else
                {
                    // 일반모드: 브라우저 활성화 + 물리 키보드
                    if (_browserHwnd != IntPtr.Zero)
                    {
                        Win32Api.SetForegroundWindow(_browserHwnd);
                        await Task.Delay(100, ct);
                    }

                    _inputSimulator.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_A);
                    await Task.Delay(100, ct);
                    _inputSimulator.Keyboard.TextEntry(username);
                    await Task.Delay(300, ct);

                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.TAB);
                    await Task.Delay(200, ct);
                    _inputSimulator.Keyboard.TextEntry(password);
                    await Task.Delay(300, ct);

                    _inputSimulator.Keyboard.KeyPress(VirtualKeyCode.RETURN);
                }
                await Task.Delay(2000, ct);

                // 로그인 성공 확인
                for (int j = 0; j < 10; j++)
                {
                    ct.ThrowIfCancellationRequested();

                    using var checkScreen = AutomationHelper.CaptureScreen(_browserHwnd, _convenienceMode);
                    using var grayLogin = new Mat();
                    Cv2.CvtColor(checkScreen, grayLogin, ColorConversionCodes.BGR2GRAY);
                    var logout = _imageMatcher.FindTemplateFromGray(grayLogin, "logout.png", scale, 0.85);
                    var lectures = _imageMatcher.FindTemplateFromGray(grayLogin, "lectures.png", scale, 0.75);

                    if (logout.Found || lectures.Found)
                    {
                        Logger.Info("로그인 성공 확인");
                        return true;
                    }

                    await Task.Delay(500, ct);
                }

                await AutomationHelper.ClosePopupsAsync(_imageMatcher, scale, _browserHwnd, _convenienceMode, _inputSimulator, ct);
                Logger.Warning("로그인 확인 실패");
                return false;
            }

            await Task.Delay(500, ct);
        }

        Logger.Error("ID 입력 필드를 찾지 못함");
        return false;
    }

    #region Private Methods

    private bool LaunchChromeWithFlags(bool startHidden = false)
    {
        try
        {
            string? chromePath = AutomationHelper.FindChromePath();
            if (chromePath == null)
            {
                Logger.Error("Chrome을 찾을 수 없습니다.");
                return false;
            }

            string profileDir = Path.Combine(Path.GetTempPath(), "DooClick_Chrome_Profile");
            InitializeChromeProfile(profileDir);

            string extDir = Path.Combine(profileDir, "Default", "Extensions", "dooclick_font", "1.0");
            string flags = $"--user-data-dir=\"{profileDir}\" " +
                          "--start-maximized " +
                          "--no-first-run " +
                          "--disable-backgrounding-occluded-windows " +
                          "--disable-renderer-backgrounding " +
                          "--disable-features=CalculateNativeWinOcclusion,WebContentsForceDark " +
                          "--disable-background-timer-throttling " +
                          $"--load-extension=\"{extDir}\"";

            if (startHidden)
            {
                flags += " --window-position=-3000,0";
                Logger.Info("Chrome 숨김 위치로 시작");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = $"{flags} {_portalUrl}",
                UseShellExecute = false
            };

            Process.Start(startInfo);
            Logger.Info($"Chrome 실행: {_portalUrl}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Chrome 실행 오류", ex);
            return false;
        }
    }

    /// <summary>
    /// Chrome 프로필 초기화 (First Run 건너뛰기 + 폰트 강제 CSS 확장 설치)
    /// </summary>
    public static void InitializeChromeProfile(string profileDir)
    {
        try
        {
            // First Run 파일 생성 → 첫 실행 마법사 건너뜀
            Directory.CreateDirectory(profileDir);
            string firstRunPath = Path.Combine(profileDir, "First Run");
            if (!File.Exists(firstRunPath))
                File.WriteAllText(firstRunPath, "");

            // 폰트 강제 CSS 확장 설치
            InstallFontExtension(profileDir);

            Logger.Info("Chrome 프로필 초기화 완료 (폰트 강제 확장 설치)");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Chrome 프로필 초기화 실패 (무시): {ex.Message}");
        }
    }

    /// <summary>
    /// 맑은 고딕 강제 CSS 확장 프로그램 설치
    /// 사이트 CSS를 !important로 오버라이드하여 모든 PC에서 동일한 폰트 렌더링 보장
    /// </summary>
    private static void InstallFontExtension(string profileDir)
    {
        string extDir = Path.Combine(profileDir, "Default", "Extensions", "dooclick_font", "1.0");
        Directory.CreateDirectory(extDir);

        // manifest.json
        string manifest = """
        {
            "manifest_version": 3,
            "name": "DooClick Font Override",
            "version": "1.0",
            "content_scripts": [{
                "matches": ["<all_urls>"],
                "css": ["font.css"],
                "run_at": "document_start"
            }]
        }
        """;
        File.WriteAllText(Path.Combine(extDir, "manifest.json"), manifest);

        // font.css - 모든 요소에 맑은 고딕 강제
        string css = """
        * {
            font-family: "Malgun Gothic", "맑은 고딕", sans-serif !important;
        }
        """;
        File.WriteAllText(Path.Combine(extDir, "font.css"), css);

        // Secure Preferences에 확장 등록 (Chrome이 확장을 인식하도록)
        string defaultDir = Path.Combine(profileDir, "Default");
        string prefsPath = Path.Combine(defaultDir, "Preferences");

        JsonNode? root = null;
        if (File.Exists(prefsPath))
        {
            string existing = File.ReadAllText(prefsPath);
            if (!string.IsNullOrWhiteSpace(existing))
                root = JsonNode.Parse(existing);
        }
        root ??= new JsonObject();

        var obj = root.AsObject();
        var extensions = EnsureJsonObject(obj, "extensions");
        var settings = EnsureJsonObject(extensions, "settings");

        // 확장 등록 정보
        settings["dooclick_font"] = JsonNode.Parse($$"""
        {
            "active_permissions": {"api":[],"manifest_permissions":[]},
            "from_webstore": false,
            "granted_permissions": {"api":[],"manifest_permissions":[]},
            "install_time": "0",
            "location": 4,
            "manifest": {
                "manifest_version": 3,
                "name": "DooClick Font Override",
                "version": "1.0",
                "content_scripts": [{"matches":["<all_urls>"],"css":["font.css"],"run_at":"document_start"}]
            },
            "path": "{{extDir.Replace("\\", "\\\\")}}",
            "state": 1,
            "was_installed_by_default": false
        }
        """);

        File.WriteAllText(prefsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    }

    private static JsonObject EnsureJsonObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var obj = new JsonObject();
        parent[key] = obj;
        return obj;
    }

    #endregion
}
