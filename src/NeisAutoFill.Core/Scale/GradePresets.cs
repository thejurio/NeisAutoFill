namespace NeisAutoFill.Core.Scale;

/// <summary>내장 기본 평가척도 프리셋. 사용자가 이를 복제·편집해 학교별 척도를 만든다.</summary>
public static class GradePresets
{
    /// <summary>프로토타입 검증 기준. 3단계 잘함/보통/노력요함.</summary>
    public static readonly GradeScale ThreeLevel = new("3단계 (잘함/보통/노력요함)", new[]
    {
        new GradeLevel("잘함",
            "매우 뛰어난 성취를 보였으므로 칭찬하고 탁월한 역량을 강조하는 긍정적 뉘앙스로 서술할 것."),
        new GradeLevel("보통",
            "보통 수준이므로 과장 없이 객관적인 관찰 사실에 기반하여 덤덤하게 기술할 것."),
        new GradeLevel("노력요함",
            "어려움을 겪으나 포기하지 않고 노력하는 태도와 향후 구체적인 발전 가능성을 격려하는 뉘앙스로 서술할 것."),
    });

    /// <summary>상/중/하 3단계.</summary>
    public static readonly GradeScale SangJungHa = new("3단계 (상/중/하)", new[]
    {
        new GradeLevel("상", "우수한 성취를 강조하는 긍정적 뉘앙스로 서술할 것."),
        new GradeLevel("중", "보통 수준으로 객관적 관찰에 기반해 덤덤하게 기술할 것."),
        new GradeLevel("하", "성장 가능성과 격려를 함께 담아 서술할 것."),
    });

    /// <summary>4단계 예시.</summary>
    public static readonly GradeScale FourLevel = new("4단계 (매우잘함/잘함/보통/노력요함)", new[]
    {
        new GradeLevel("매우잘함", "탁월한 성취를 강조하는 긍정적 뉘앙스로 서술할 것."),
        new GradeLevel("잘함", "뛰어난 성취를 칭찬하는 뉘앙스로 서술할 것."),
        new GradeLevel("보통", "보통 수준으로 객관적 관찰에 기반해 기술할 것."),
        new GradeLevel("노력요함", "성장 가능성과 격려를 함께 담아 서술할 것."),
    });

    /// <summary>5단계 예시.</summary>
    public static readonly GradeScale FiveLevel = new("5단계 (매우잘함/잘함/보통/미흡/노력요함)", new[]
    {
        new GradeLevel("매우잘함", "탁월한 성취를 강조하는 긍정적 뉘앙스로 서술할 것."),
        new GradeLevel("잘함", "뛰어난 성취를 칭찬하는 뉘앙스로 서술할 것."),
        new GradeLevel("보통", "보통 수준으로 객관적 관찰에 기반해 기술할 것."),
        new GradeLevel("미흡", "부족한 부분을 지적하되 발전 방향을 함께 제시할 것."),
        new GradeLevel("노력요함", "성장 가능성과 격려를 함께 담아 서술할 것."),
    });

    public static IReadOnlyList<GradeScale> All => new[]
    {
        ThreeLevel, SangJungHa, FourLevel, FiveLevel,
    };
}
