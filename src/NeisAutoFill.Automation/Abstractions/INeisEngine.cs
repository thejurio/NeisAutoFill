using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Automation.Abstractions;

/// <summary>실시간 진행 상황 한 건 (로그 라인 + 선택적 진행 카운트).</summary>
public sealed record ProgressInfo(string Message, int? Current = null, int? Total = null);

/// <summary>화면 파악 결과 — UI 가 매칭을 검토·결정할 재료.</summary>
public sealed record MatchContext(
    string? ScreenSubject,
    string TargetSubject,
    IReadOnlyDictionary<int, RowMeta> RowMap,
    IReadOnlyList<int> MissingRows);

/// <summary>UI 가 확정한 매칭 방식. null 반환 = 사용자 취소.</summary>
/// <param name="AreaMap">이름 기반: 화면 영역명 → 엑셀 영역명 ("" = 제외)</param>
/// <param name="NameMap">화면 학생이름 → 엑셀 학생이름 ("" = 제외)</param>
/// <param name="OrderedExcelAreas">순서 기반: 화면 행 순서별 엑셀 영역 ("" = 건너뜀)</param>
public sealed record MatchDecision(
    Core.Matching.StudentMatcher.MatchMode Mode,
    IReadOnlyDictionary<string, string>? AreaMap = null,
    IReadOnlyDictionary<string, string>? NameMap = null,
    IReadOnlyList<string>? OrderedExcelAreas = null);

/// <summary>나이스 자동입력 엔진. GUI(ViewModel)는 이 인터페이스에만 의존.</summary>
public interface INeisEngine
{
    bool Connected { get; }

    /// <summary>디버그 모드 Edge 실행 (§6 ①). 사용자가 로그인·조회를 직접 수행.</summary>
    void LaunchEdge();

    /// <summary>실행된 Edge 에 CDP attach + neis.go.kr 탭 선택.</summary>
    Task<bool> AttachAsync(CancellationToken ct = default);

    /// <summary>연결 생존 확인 — 죽었으면 내부 상태를 비우고 false 반환.</summary>
    Task<bool> IsAliveAsync();

    /// <summary>현재 화면의 교과(과목)명 (§3.2). 없으면 null.</summary>
    Task<string?> GetCurrentSubjectAsync(CancellationToken ct = default);

    /// <summary>화면 과목 콤보의 선택 가능한 과목 목록을 읽는다 (전과목 매핑용). 콤보를 못 찾으면 빈 목록.</summary>
    Task<IReadOnlyList<string>> ReadSubjectOptionsAsync(CancellationToken ct = default);

    /// <summary>화면 상단 과목 콤보로 과목을 전환하고 [조회] 후 갱신을 기다린다 (Phase 5.5).</summary>
    Task<(bool Ok, string Why)> SelectSubjectAsync(string subjectName, CancellationToken ct = default);

    /// <summary>조회조건의 학년·반 콤보를 목표값으로 맞추고 [조회] 한다 (전담 F9 M6).
    /// 이미 해당 학년·반이면 조회를 생략(안전). progress 로 찾은 콤보·선택 과정을 보고한다.</summary>
    Task<(bool Ok, string Why)> SelectClassAsync(
        int grade, string @class, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default);

    /// <summary>현재 화면의 [저장] 버튼을 눌러 저장한다 (전과목 자동 업로드 전용 — A안).
    /// 저장 확인/완료 대화상자가 뜨면 긍정 버튼을 눌러 마무리.</summary>
    Task<(bool Ok, string Why)> SaveScreenAsync(CancellationToken ct = default);

    /// <summary>한 과목 전체 실행 (§4.1). 저장은 하지 않는다.</summary>
    /// <param name="resolveMatch">
    /// 화면 파악 후 매칭 확정 콜백. 문제(과목·이름·영역 불일치)를 UI 가 검토해 결정을 돌려준다.
    /// null 반환 = 취소. 콜백이 없으면(null) 화면·엑셀이 정확히 일치할 때만 진행한다.
    /// </param>
    Task<RunReport> RunSubjectAsync(
        SubjectSheet sheet,
        GradeScale scale,
        bool dryRun,
        IProgress<ProgressInfo> progress,
        Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null,
        CancellationToken ct = default);

    /// <summary>현재 화면 구조 진단 리포트 (Phase 8 셀렉터 실측용, 읽기 전용).</summary>
    Task<string> InspectDomAsync(CancellationToken ct = default);

    /// <summary>AI 생성 서술문을 화면 textarea 에 입력 (Phase 8). 저장은 하지 않는다.
    /// resolveMatch: 이름이 달라 자동 매칭 안 되는 학생을 사용자가 매핑 (등급과 동일 콜백). null=매핑 없이 자동만.</summary>
    Task<NarrativeReport> RunNarrativesAsync(
        string subjectName,
        IReadOnlyList<NarrativeEntry> entries,
        bool dryRun,
        int maxBytes,
        IProgress<ProgressInfo> progress,
        CancellationToken ct = default,
        Func<MatchContext, Task<MatchDecision?>>? resolveMatch = null);
}
