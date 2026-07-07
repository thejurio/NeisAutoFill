namespace NeisAutoFill.Core.Models;

/// <summary>
/// AI 서술문 생성에 넘길 한 영역의 성취 내역. (Index.html st.domains 와 동형)
/// </summary>
public sealed record DomainPoint(
    string DomainName,
    string Grade,
    string CriteriaText,
    string? Achievement);
