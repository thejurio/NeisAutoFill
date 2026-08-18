namespace NeisAutoFill.Automation;

/// <summary>
/// 시간표 화면을 다루는 읽기 전용 도구 묶음.
/// App 이 <c>IPage</c> 를 직접 만지지 않도록 엔진이 이것만 넘긴다(의존 방향 유지).
/// </summary>
/// <param name="Reader">주차·셀·카탈로그 읽기</param>
/// <param name="Scope">조회 조건·학교·사용자 범위</param>
/// <param name="Diagnostics">비식별 진단 리포트</param>
public sealed record TimetableTools(
    TimetableReader Reader,
    TimetableScopeReader Scope,
    TimetableDiagnostics Diagnostics);
