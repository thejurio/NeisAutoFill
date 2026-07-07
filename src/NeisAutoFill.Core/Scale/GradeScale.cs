namespace NeisAutoFill.Core.Scale;

/// <summary>
/// 평가척도의 한 단계. 학교마다 다른 등급 체계를 데이터로 표현한다.
/// Label 은 엑셀 표기이자 나이스 드롭다운 텍스트 (둘은 항상 동일하게 운용).
/// </summary>
/// <param name="Label">등급 표기 (예: "잘함", "상", "매우잘함").</param>
/// <param name="AiNuance">AI 서술 방향 지시문 (code.gs 하드코딩 뉘앙스를 데이터화).</param>
public sealed record GradeLevel(
    string Label,
    string AiNuance = "");

/// <summary>순서 있는 등급 목록으로 정의된 평가척도. Levels 순서 = 상위→하위.</summary>
public sealed record GradeScale(string Name, IReadOnlyList<GradeLevel> Levels)
{
    /// <summary>주어진 라벨이 이 척도의 유효 등급인지.</summary>
    public bool Contains(string label) =>
        Levels.Any(l => l.Label == label);

    /// <summary>라벨로 레벨 조회 (없으면 null).</summary>
    public GradeLevel? Find(string label) =>
        Levels.FirstOrDefault(l => l.Label == label);

    /// <summary>모든 유효 라벨 집합 (엑셀 등급 감지·화이트리스트용).</summary>
    public IReadOnlySet<string> Labels =>
        Levels.Select(l => l.Label).ToHashSet();
}
