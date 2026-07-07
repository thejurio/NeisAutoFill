using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Generator;

/// <summary>
/// 등급 → AI 서술 뉘앙스 해석. 프롬프트 본문은 GAS(code.gs) 쪽에서 조립하고,
/// 프로그램은 척도의 등급별 뉘앙스 맵만 실어 보낸다.
/// </summary>
public static class PromptBuilder
{
    public static string ResolveNuance(string grade, GradeScale scale)
    {
        var nuance = scale.Find(grade)?.AiNuance;
        return string.IsNullOrWhiteSpace(nuance)
            ? "과장 없이 객관적인 관찰 사실에 기반하여 덤덤하게 기술할 것."
            : nuance!;
    }
}
