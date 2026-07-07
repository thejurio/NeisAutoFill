namespace NeisAutoFill.Core.Models;

/// <summary>나이스 그리드 한 행에서 파싱한 메타(번호/성명/영역). §3.3 aria-label 기반.</summary>
public sealed record RowMeta(string? No, string? Name, string? Area);
