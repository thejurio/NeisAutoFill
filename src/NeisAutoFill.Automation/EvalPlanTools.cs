namespace NeisAutoFill.Automation;

/// <summary>
/// 평가계획 화면을 다루는 도구 묶음.
/// App 이 <c>IPage</c> 를 직접 만지지 않도록 엔진이 이것만 넘긴다(의존 방향 유지).
/// </summary>
/// <param name="Reader">화면 판별·조회·그리드 읽기</param>
/// <param name="Writer">영역명·성취기준·평가기준 입력과 저장</param>
public sealed record EvalPlanTools(EvalScreenReader Reader, EvalPlanWriter Writer);
