using NeisAutoFill.Core.Models;
using NeisAutoFill.Core.Scale;

namespace NeisAutoFill.Generator;

/// <summary>교과학습발달상황 서술문 생성기. GAS 백엔드/직접 Gemini 두 구현을 설정으로 전환.</summary>
public interface IEvaluationGenerator
{
    Task<string> GenerateAsync(
        string studentName,
        string subjectName,
        IReadOnlyList<DomainPoint> domains,
        string? subjectNote,
        GradeScale scale,
        CancellationToken ct = default);
}
