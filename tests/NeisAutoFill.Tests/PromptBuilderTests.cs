using NeisAutoFill.Core.Scale;
using NeisAutoFill.Generator;
using Xunit;

namespace NeisAutoFill.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Injects_scale_nuance_per_grade()
    {
        var scale = new GradeScale("커스텀", new[]
        {
            new GradeLevel("최고", "매우 특별한 뉘앙스 지시문."),
            new GradeLevel("기본"),   // 뉘앙스 미지정 → 기본 문구
        });

        Assert.Equal("매우 특별한 뉘앙스 지시문.", PromptBuilder.ResolveNuance("최고", scale));
        Assert.Contains("객관적인 관찰", PromptBuilder.ResolveNuance("기본", scale));
    }

    [Fact]
    public void Unknown_grade_falls_back_to_default_nuance()
    {
        Assert.Contains("객관적인 관찰",
            PromptBuilder.ResolveNuance("없는등급", GradePresets.ThreeLevel));
    }
}
