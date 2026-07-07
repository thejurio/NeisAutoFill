using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Automation.Abstractions;

/// <summary>실시간 진행 상황 한 건 (로그 라인 + 선택적 진행 카운트).</summary>
public sealed record ProgressInfo(string Message, int? Current = null, int? Total = null);

/// <summary>나이스 자동입력 엔진. GUI(ViewModel)는 이 인터페이스에만 의존.</summary>
public interface INeisEngine
{
    bool Connected { get; }

    /// <summary>디버그 모드 Edge 실행 (§6 ①). 사용자가 로그인·조회를 직접 수행.</summary>
    void LaunchEdge();

    /// <summary>실행된 Edge 에 CDP attach + neis.go.kr 탭 선택.</summary>
    Task<bool> AttachAsync(CancellationToken ct = default);

    /// <summary>현재 화면의 교과(과목)명 (§3.2). 없으면 null.</summary>
    Task<string?> GetCurrentSubjectAsync(CancellationToken ct = default);

    /// <summary>한 과목 전체 실행 (§4.1). 저장은 하지 않는다.</summary>
    Task<RunReport> RunSubjectAsync(
        SubjectSheet sheet,
        GradeScale scale,
        bool dryRun,
        IProgress<ProgressInfo> progress,
        CancellationToken ct = default);

    /// <summary>현재 화면 구조 진단 리포트 (Phase 8 셀렉터 실측용, 읽기 전용).</summary>
    Task<string> InspectDomAsync(CancellationToken ct = default);

    /// <summary>AI 생성 서술문을 화면 textarea 에 입력 (Phase 8). 저장은 하지 않는다.</summary>
    Task<NarrativeReport> RunNarrativesAsync(
        string subjectName,
        IReadOnlyList<NarrativeEntry> entries,
        bool dryRun,
        int maxBytes,
        IProgress<ProgressInfo> progress,
        CancellationToken ct = default);
}
