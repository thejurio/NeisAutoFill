using System.Net.Http;
using System.Windows.Threading;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;
using NeisAutoFill.Generator;

namespace NeisAutoFill.App.Services;

/// <summary>생성 작업 한 건 — 화면 항목과 독립적으로 필요한 데이터를 모두 담는다.</summary>
public sealed record GenJob(
    string Subject, string No, string Name,
    IReadOnlyList<DomainPoint> Domains, string? Note, GradeScale Scale,
    string? VariationHint = null);   // 있으면 "다르게 다시 생성" — 표현을 달리하라는 추가 지시

/// <summary>
/// AI 서술문 백그라운드 생성 큐. 창을 닫거나 과목을 전환해도 계속 돌고,
/// 완료 즉시 NarrativeStore 에 저장되므로 결과를 잃지 않는다.
/// 동시 실행은 GAS 부하를 고려해 2건으로 제한. 이벤트·store 접근은 모두 UI 스레드로 마셜링.
/// </summary>
public sealed class GenerationQueue
{
    private const int MaxConcurrent = 2;
    private static HttpClient Http => AppHttp.Long;   // 생성 호출은 김 (GAS)

    private readonly GeneratorSettingsStore _settings;
    private readonly NarrativeStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly object _gate = new();
    private readonly Queue<GenJob> _pending = new();
    private int _workers;
    private CancellationTokenSource _cts = new();

    // 배치 사용 로그(완료 시 1건) — 과목별 인원 누계 + 실제로 쓴 키 뒤 4자리
    private readonly Dictionary<string, int> _batchCounts = new();
    private readonly SortedSet<string> _batchKeys = new(StringComparer.Ordinal);

    private readonly UsageLogger _usage;

    public GenerationQueue(GeneratorSettingsStore settings, NarrativeStore store, UsageLogger usage)
    {
        _settings = settings;
        _store = store;
        _usage = usage;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    /// <summary>워커가 작업을 집어 실제 생성을 시작할 때 (UI 스레드) — "대기 중" → "생성 중" 표시용.</summary>
    public event Action<GenJob>? JobStarted;
    /// <summary>완료/실패 한 건마다 (UI 스레드). ok=true 면 text 는 서술문, false 면 오류 메시지.</summary>
    public event Action<GenJob, string, bool>? JobFinished;
    /// <summary>카운터 변경마다 (UI 스레드).</summary>
    public event Action? StateChanged;
    public event Action<string>? Log;

    public int Done { get; private set; }
    public int Failed { get; private set; }
    public int Total { get; private set; }
    public bool IsBusy { get { lock (_gate) return _pending.Count > 0 || _workers > 0; } }

    public string Status
    {
        get
        {
            if (Total == 0) return "";
            var s = $"서술문 생성 {Done + Failed}/{Total}" + (Failed > 0 ? $" (실패 {Failed})" : "");
            return IsBusy ? s + " — 창을 닫아도 계속 진행됩니다" : s + " · 완료";
        }
    }

    public void Enqueue(IEnumerable<GenJob> jobs)
    {
        var list = jobs.ToList();
        int added = 0;
        lock (_gate)
        {
            if (_pending.Count == 0 && _workers == 0)   // 새 배치 — 카운터·로그 누계 초기화
            {
                Done = 0; Failed = 0; Total = 0;
                _batchCounts.Clear();
                _batchKeys.Clear();
            }
            if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            foreach (var j in list)
            {
                _pending.Enqueue(j);
                added++;
                _batchCounts[j.Subject] = _batchCounts.GetValueOrDefault(j.Subject) + 1;
            }
            Total += added;
            while (_workers < MaxConcurrent && _pending.Count > _workers)
            {
                _workers++;
                _ = Task.Run(WorkerLoopAsync);
            }
        }
        if (added == 0) return;
        // 사용 기록은 학생당이 아니라 배치당 1건 — 실제로 쓴 키를 알아야 하므로 완료 시(WorkerLoop) 전송
        RaiseState();
    }

    public void CancelAll()
    {
        int dropped;
        lock (_gate)
        {
            dropped = _pending.Count;
            _pending.Clear();
            Total -= dropped;
            _cts.Cancel();
        }
        Log?.Invoke($"서술문 생성 중지 — 대기 {dropped}건 취소, 진행 중이던 요청은 곧 멈춥니다.");
        RaiseState();
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            GenJob job;
            CancellationToken ct;
            lock (_gate)
            {
                if (_pending.Count == 0) { _workers--; break; }
                job = _pending.Dequeue();
                ct = _cts.Token;
            }

            await _dispatcher.InvokeAsync(() => JobStarted?.Invoke(job));

            string text;
            bool ok = false;
            try
            {
                var gen = new GasBackendGenerator(Http, _settings.Options);
                text = await gen.GenerateAsync(job.Name, job.Subject, job.Domains, job.Note, job.Scale, ct, job.VariationHint);
                ok = !string.IsNullOrWhiteSpace(text);
                if (!ok) text = "서버가 빈 서술문을 반환했습니다.";
                if (!string.IsNullOrEmpty(gen.LastKeyHint))
                    lock (_gate) _batchKeys.Add(gen.LastKeyHint);   // 이 배치에서 실제로 쓴 키
            }
            catch (OperationCanceledException) { text = "중지됨"; }
            catch (Exception ex) { text = ex.Message; }

            var (j, payload, completed) = (job, text, ok);
            await _dispatcher.InvokeAsync(() =>
            {
                if (completed) { Done++; _store.Set(j.Subject, j.No, j.Name, payload); }
                else Failed++;
                JobFinished?.Invoke(j, payload, completed);
                StateChanged?.Invoke();
                if (!IsBusy)
                    Log?.Invoke($"서술문 생성 배치 완료: 성공 {Done} / 실패 {Failed}");
                if (Total > 0 && Done + Failed >= Total)
                    SendBatchLog();   // 배치당 1건 — F열에 실제로 쓴 키 (워커 타이밍과 무관하게 마지막 완료 시)
            });
        }
        RaiseState();
    }

    /// <summary>배치 완료 시 사용 기록 1건 전송 (예: "국어 24명 · 수학 20명", F열=쓴 키들).</summary>
    private void SendBatchLog()
    {
        string info, keyHint;
        lock (_gate)
        {
            if (_batchCounts.Count == 0) return;
            info = string.Join(" · ", _batchCounts.Select(kv => $"{kv.Key} {kv.Value}명"));
            keyHint = string.Join(",", _batchKeys);
        }
        _ = _usage.LogBatchAsync(info, keyHint);
    }

    private void RaiseState()
    {
        if (_dispatcher.CheckAccess()) StateChanged?.Invoke();
        else _dispatcher.InvokeAsync(() => StateChanged?.Invoke());
    }
}
