using System.Windows;
using NeisAutoFill.App.ViewModels;
using NeisAutoFill.Automation;
using NeisAutoFill.Automation.Abstractions;

namespace NeisAutoFill.App.Helpers;

/// <summary>
/// 배치 입력 공통 뼈대 (R10) — 4벌(담임/전담 × 성적/서술문)이 공유:
/// [화면 과목 매핑 제안] → 선택 창 → 대상 목록 → 러너(전환·검증·자동 저장 정책) → 요약 로그 → 결과 대시보드(재시도).
/// 대상 하나의 준비·실행(RunTarget)만 호출부가 주입한다 — 창 문구·재시도 배선을 바꿀 땐 여기 한 곳만.
/// </summary>
internal sealed class BatchUploadFlow
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    /// <summary>대상 명사 — 담임 배치 "과목", 전담 배치 "반" (자동 저장 경고 문구에 쓰임).</summary>
    public required string TargetNoun { get; init; }
    /// <summary>요약 단위 — 성적 "건", 서술문 "명".</summary>
    public required string Unit { get; init; }
    /// <summary>요약 로그 제목 (예: "전과목 자동 입력 결과").</summary>
    public required string SummaryTitle { get; init; }
    public required INeisEngine Engine { get; init; }
    public required Action<string> Log { get; init; }
    /// <summary>새 취소 토큰 발급 — VM 이 자기 [■ 중지] 버튼용 CTS 를 갈아끼우고 토큰을 돌려준다 (재시도 때도 호출).</summary>
    public required Func<CancellationToken> NewCts { get; init; }
    /// <summary>대상 하나 실행 (준비·입력). 러너가 전환 후 Display 이름으로 부른다.</summary>
    public required Func<string, Task<BatchUploadRunner.SubjectResult>> RunTarget { get; init; }
    /// <summary>러너가 과목 콤보를 전환할지 — 대상이 반("3-1")이면 false (이동·전환은 RunTarget 안에서).</summary>
    public bool SwitchSubjects { get; init; } = true;
    /// <summary>화면 과목 콤보를 읽어 (내 과목 → 화면 과목) 매핑 제안을 채울지 (담임 배치만 — 화면에 도착한 뒤 호출할 것).</summary>
    public bool MapScreenSubjects { get; init; }
    /// <summary>입력 시작 직전 훅 (배치 이름 매핑 캐시 초기화 등).</summary>
    public Action? OnStart { get; init; }
    /// <summary>매칭 확인 창구 — 지정하면 선택 창의 (내 과목 → 화면 과목) 매핑을 넘겨,
    /// 사용자가 이미 확정한 과목은 입력 단계에서 "그대로 진행?"을 다시 묻지 않는다.</summary>
    public MatchSession? Session { get; init; }
    /// <summary>실행 중 표시 토글 (생성기 IsUploading — 재시도 때도 켜진다).</summary>
    public Action<bool>? Running { get; init; }

    /// <summary>선택 창을 띄우고 고른 대상들을 순회 입력한다. picks 는 호출부가 대상별로 만들어 온다.</summary>
    public async Task RunAsync(IReadOnlyList<SubjectPick> picks)
    {
        if (MapScreenSubjects) await PopulateScreenMappingAsync(picks);

        var win = new BatchGenerateWindow(picks,
            title: Title,
            description: Description,
            startLabel: "🚀 입력 시작",
            warning: $"각 {TargetNoun} 입력 후 검증을 통과하면 나이스 [저장]을 자동으로 누르고 다음 {TargetNoun}으로 넘어갑니다. " +
                     $"검증에 실패한 {TargetNoun}은 저장하지 않고 그 자리에서 중단합니다.")
        { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;

        var chosen = picks.Where(p => p.IsChecked).ToList();
        if (chosen.Count == 0) return;
        var targets = BuildTargets(chosen);

        OnStart?.Invoke();   // Reset 후에 매핑을 넘겨야 지워지지 않는다
        Session?.AcceptSubjects(targets.ToDictionary(t => t.Display, t => t.Screen));
        var outcomes = await RunOnceAsync(targets);

        Log(SummaryTitle + ":");
        foreach (var s in BatchUploadRunner.Summarize(outcomes)) Log("  " + s);

        // 재시도: 실패·미도달 대상만 같은 경로로 (표시명 → 매핑된 화면명 복원)
        var screenByDisplay = targets.ToDictionary(t => t.Display, t => t.Screen);
        BatchResultWindow.ShowResult(outcomes, Unit,
            retry: async subs => await RunOnceAsync(subs.Select(d => new BatchUploadRunner.SubjectTarget(
                d, screenByDisplay.TryGetValue(d, out var sc) ? sc : d)).ToList()),
            owner: Application.Current.MainWindow);
    }

    private async Task<List<BatchUploadRunner.SubjectOutcome>> RunOnceAsync(
        IReadOnlyList<BatchUploadRunner.SubjectTarget> targets)
    {
        var ct = NewCts();
        Running?.Invoke(true);
        try { return await BatchUploadRunner.RunAsync(targets, Engine, RunTarget, Log, Unit, ct, SwitchSubjects); }
        finally { Running?.Invoke(false); }
    }

    /// <summary>선택된 대상들로 (표시명 → 화면 과목) 목록. 매핑이 없거나 '미선택'이면 같은 이름 (반 대상은 항상 같은 이름).</summary>
    private static List<BatchUploadRunner.SubjectTarget> BuildTargets(IEnumerable<SubjectPick> chosen) =>
        chosen.Select(p =>
        {
            var screen = p.HasScreenOptions && p.ScreenSubject != SubjectPick.NoScreenMatch
                ? p.ScreenSubject : p.Name;
            return new BatchUploadRunner.SubjectTarget(p.Name, screen);
        }).ToList();

    /// <summary>화면 과목 콤보 목록을 읽어 각 pick 에 자동 매핑 제안을 채운다. 못 읽으면(오프라인 등) 매핑 UI 없이 같은 이름.
    /// ★ 이전 작업에서 취소된 토큰을 쓰면 콤보 읽기가 즉시 취소돼 매핑이 안 뜬다 → 토큰 없이 읽는다.</summary>
    private async Task PopulateScreenMappingAsync(IReadOnlyList<SubjectPick> picks)
    {
        try
        {
            var screen = await Engine.ReadSubjectOptionsAsync();
            if (screen.Count == 0) return;
            var suggestions = Core.SubjectMapper.Suggest(picks.Select(p => p.Name).ToList(), screen);
            for (int i = 0; i < picks.Count; i++)
                picks[i].SetScreenMapping(screen, suggestions[i].Screen, suggestions[i].Auto);
        }
        catch (Exception ex) { Services.Diag.Swallow(ex, "배치 화면 과목 매핑"); }   // 같은 이름으로 진행
    }
}
