using System.Windows;
using System.Windows.Media;
using NeisAutoFill.App.Mvvm;
using NeisAutoFill.Automation.Abstractions;

namespace NeisAutoFill.App.Services;

/// <summary>
/// 나이스 연결·상태 전담 (R9) — 자동 연결 루프, 상황 판별(DetectStatus) 반영,
/// 상태칩(색·글자)·안내 배너 값, 입력 전 사전 점검+화면 이동 게이트를 한 곳으로.
/// MainViewModel 은 이 컨트롤러의 값을 바인딩으로 위임만 하고,
/// 입력 경로(성적·서술문 × 담임·전담)는 EnsureReadyAsync 하나로 사전 점검한다.
/// </summary>
public sealed class NeisSessionController : ObservableObject
{
    private readonly INeisEngine _engine;
    private readonly CancellationTokenSource _cts = new();

    public NeisSessionController(INeisEngine engine) => _engine = engine;

    /// <summary>연결·끊김 같은 사용자에게 보일 사건만 메인 로그로 (MainVM 이 구독).</summary>
    public event Action<string>? Log;

    /// <summary>앱 시작부터 자동 연결·재연결 루프 가동. 종료 시 함께 정리.</summary>
    public void Start()
    {
        Application.Current.Exit += (_, _) => _cts.Cancel();
        _ = LoopAsync(_cts.Token);
    }

    /// <summary>
    /// 백그라운드 자동 연결 루프. 안 붙어 있으면 조용히 attach 시도(이미 열린 attach 가능 브라우저 자동 포착),
    /// 붙어 있으면 상황(로그인·화면 종류)까지 판별해 상태를 갱신. 사용자가 연결 버튼을 따로 누를 필요가 없다.
    /// </summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_engine.Connected)
                {
                    try
                    {
                        await _engine.AttachAsync(ct).ConfigureAwait(false);
                        // 연결 직후에도 바로 상태 판별 — 로그인 전인데 '연결됨'으로 잠깐 뜨는 문제 방지
                        var first = await _engine.DetectStatusAsync(ct).ConfigureAwait(false);
                        Ui(() => { ApplyStatus(first); Log?.Invoke("브라우저 자동 연결됨."); });
                    }
                    catch (Exception ex)
                    {
                        Diag.Swallow(ex, "자동연결 attach");   // 조용히 재시도하되, 사용자에겐 다음 할 일을 안내
                        // 브라우저 자체가 없음 vs 열렸지만 나이스 탭 없음 을 구분해 안내 (우리가 던진 메시지 기준)
                        var hint = ex is InvalidOperationException && (ex.Message.Contains("neis") || ex.Message.Contains("탭"))
                            ? "나이스 전용 브라우저는 열렸어요. 나이스에 로그인하고 [교과별 평가](또는 [학기말 종합의견])를 조회하면 자동으로 연결됩니다."
                            : "아직 연결되지 않았어요. [🌐 NEIS 접속] 버튼으로 전용 브라우저를 여세요. (평소 쓰던 Edge 를 그냥 열면 연결되지 않습니다)";
                        Ui(() => ConnectionHint = hint);
                    }
                }
                else
                {
                    var status = await _engine.DetectStatusAsync(ct).ConfigureAwait(false);
                    Ui(() =>
                    {
                        var wasConnected = IsConnected;
                        ApplyStatus(status);
                        if (wasConnected && status.Kind == NeisScreenKind.Disconnected)
                            Log?.Invoke("브라우저 연결이 끊어졌습니다. 재연결을 시도합니다.");
                    });
                }
            }
            catch (Exception ex) { Diag.Swallow(ex, "자동연결 루프"); }   // 루프는 어떤 경우에도 죽지 않는다

            try { await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.Invoke(a);

    // ── 상태칩·안내 값 (MainVM 이 그대로 바인딩 위임) ──────────

    private static readonly Color StatusGreen = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color StatusAmber = Color.FromRgb(0xF5, 0x9E, 0x0B);
    private static readonly Color StatusRed = Color.FromRgb(0xEF, 0x44, 0x44);

    private bool _isConnected;
    /// <summary>나이스 연결 여부 — 입력 버튼 활성/[NEIS 접속] 버튼 표시 제어 (U3·U5).</summary>
    public bool IsConnected { get => _isConnected; private set => SetProperty(ref _isConnected, value); }

    /// <summary>나이스가 입력 준비(교과별 평가 화면) 상태인지 — 입력 전 사전 점검·안내용.</summary>
    public bool NeisReady { get; private set; }

    private string _connectionText = "미연결";
    public string ConnectionText { get => _connectionText; private set => SetProperty(ref _connectionText, value); }

    private Brush _connectionBrush = new SolidColorBrush(StatusRed);
    public Brush ConnectionBrush { get => _connectionBrush; private set => SetProperty(ref _connectionBrush, value); }

    private string _connectionHint = "";
    /// <summary>미연결 시 다음에 뭘 해야 하는지 안내 (실패 원인별). 연결되면 빈 문자열.</summary>
    public string ConnectionHint { get => _connectionHint; private set => SetProperty(ref _connectionHint, value); }

    /// <summary>판별된 상황을 상태칩(색·글자)과 안내 배너에 반영 (F9 M8).</summary>
    private void ApplyStatus(NeisStatus s)
    {
        switch (s.Kind)
        {
            case NeisScreenKind.Disconnected:
                NeisReady = false; SetConnected(false); break;

            case NeisScreenKind.NotNeisTab:
                SetStatus(false, "나이스 열기", StatusAmber, ""); break;

            case NeisScreenKind.LoggedOut:
                SetStatus(false, "로그인 필요", StatusAmber, ""); break;

            // 교과별 평가 화면이든 아니든 로그인돼 있으면 '연결됨' — 화면 이동은 앱이 알아서 한다
            case NeisScreenKind.OtherNeisPage:
            case NeisScreenKind.EvaluationReady:
                SetStatus(true, "연결됨", StatusGreen, ""); break;
        }
    }

    private void SetConnected(bool on)
    {
        ConnectionText = on ? "연결됨" : "미연결";
        ConnectionBrush = new SolidColorBrush(on ? StatusGreen : StatusRed);
        IsConnected = on;
        if (on) ConnectionHint = "";   // 연결되면 안내 배너 숨김
    }

    /// <summary>상태칩·안내를 한 번에 설정. ready = 교과별 평가 화면이라 입력 가능.
    /// 브라우저는 붙어 있으므로 IsConnected(=버튼 활성)는 true 유지하되, 준비 여부는 NeisReady 로 구분.</summary>
    private void SetStatus(bool ready, string text, Color color, string hint)
    {
        ConnectionText = text;
        ConnectionBrush = new SolidColorBrush(color);
        NeisReady = ready;
        IsConnected = true;                 // 브라우저는 연결됨 — 버튼은 활성, 부적절하면 입력 시 안내
        ConnectionHint = ready ? "" : hint;
    }

    // ── 입력 전 사전 점검 + 화면 이동 게이트 ──────────────────

    private const string NotConnectedMessage =
        "나이스에 아직 연결되지 않았습니다. 메인 화면의 [🌐 NEIS 접속]으로 브라우저를 열고 로그인·조회하면 자동으로 연결됩니다.";

    /// <summary>연결만 빠르게 점검 — 안 붙어 있으면 안내 문구, 붙어 있으면 null.
    /// (화면 이동 없이 시작 가능 여부만 볼 때 — 이동까지 필요하면 EnsureReadyAsync)</summary>
    public string? ConnectCheck() => _engine.Connected ? null : NotConnectedMessage;

    /// <summary>입력 전 사전 점검 + 필요 시 화면 이동. 진행 가능하면 null, 사용자가 풀어야 하는
    /// 상황(로그인 안 됨·브라우저/탭)이면 안내 문구를 돌려준다 — 표시는 호출부 방식대로.
    /// 화면이 다른 건 앱이 스스로 이동한다 — 메시지를 띄우지 않는다 (F9 M8).</summary>
    public async Task<string?> EnsureReadyAsync(NeisTarget target,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        if (!_engine.Connected) return NotConnectedMessage;

        var status = await _engine.DetectStatusAsync(ct);
        if (status.Kind is NeisScreenKind.Disconnected or NeisScreenKind.NotNeisTab or NeisScreenKind.LoggedOut)
            return StatusMessage(status.Kind);

        // 이미 목적 화면이면 이동 생략 (교과별 평가만 상태로 알 수 있음 —
        // 그 외 화면은 NavigateToAsync 가 제목(app-tit)으로 스스로 생략 판단)
        if (target == NeisTarget.Evaluation && status.Kind == NeisScreenKind.EvaluationReady)
            return null;

        if (!await _engine.NavigateToAsync(target, progress, ct))
            return target switch
            {
                NeisTarget.Evaluation => "교과별 평가 화면으로 이동하지 못했어요. 나이스에서 [교과별 평가]를 열어 주세요.",
                NeisTarget.TermOpinion => "학기말종합의견 화면으로 이동하지 못했어요. 나이스에서 직접 열어 주세요.",
                _ => "입력 화면으로 이동하지 못했어요. 나이스에서 직접 열어 주세요.",
            };
        return null;
    }

    /// <summary>상황별 입력 불가 안내 — 상황만 담백하게 (사용자가 할 일 나열 금지, F9 M8).</summary>
    public static string StatusMessage(NeisScreenKind kind) => kind switch
    {
        NeisScreenKind.Disconnected => "나이스에 연결되어 있지 않습니다.",
        NeisScreenKind.NotNeisTab => "현재 탭이 나이스가 아닙니다.",
        NeisScreenKind.LoggedOut => "나이스에 로그인되어 있지 않습니다.",
        NeisScreenKind.OtherNeisPage => "교과별 평가 화면이 아닙니다.",
        _ => "입력할 수 있는 상태가 아닙니다.",
    };
}
