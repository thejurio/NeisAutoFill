using System.Collections.Generic;
using NeisAutoFill.Generator;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F4 — 과목별 목표 글자 수 override 해석.</summary>
public class SubjectTargetCharsTests
{
    [Fact]
    public void 과목별_지정이_있으면_그값()
    {
        var o = new GeneratorOptions
        {
            TargetChars = 150,
            SubjectTargetChars = new Dictionary<string, int> { ["국어"] = 250 },
        };
        Assert.Equal(250, o.TargetCharsFor("국어"));
    }

    [Fact]
    public void 과목별_지정이_없으면_전역값()
    {
        var o = new GeneratorOptions { TargetChars = 150 };
        Assert.Equal(150, o.TargetCharsFor("수학"));
    }

    [Fact]
    public void 과목별_지정이_0이면_전역값_사용()
    {
        var o = new GeneratorOptions
        {
            TargetChars = 150,
            SubjectTargetChars = new Dictionary<string, int> { ["체육"] = 0 },
        };
        Assert.Equal(150, o.TargetCharsFor("체육"));   // 0 = 상속
    }

    [Fact]
    public void 전역도_0이고_지정도_없으면_0_AI자율()
    {
        var o = new GeneratorOptions();
        Assert.Equal(0, o.TargetCharsFor("국어"));
    }
}
