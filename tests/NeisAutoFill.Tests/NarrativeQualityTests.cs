using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F6 — 서술문 품질(학생 간 복붙 의심) 검출.</summary>
public class NarrativeQualityTests
{
    [Fact]
    public void 동일한_서술문은_한_그룹으로_묶인다()
    {
        var texts = new[]
        {
            "수업에 적극적으로 참여하며 발표를 잘함.",
            "수업에 적극적으로 참여하며 발표를 잘함.",
        };
        var groups = NarrativeQuality.SimilarGroups(texts);
        var g = Assert.Single(groups);
        Assert.Equal(new[] { 0, 1 }, g);
    }

    [Fact]
    public void 서로_다른_서술문은_묶이지_않는다()
    {
        var texts = new[]
        {
            "세 자리 수의 덧셈과 뺄셈을 정확하게 계산함.",
            "이야기를 듣고 중심 내용을 파악하여 발표함.",
            "리듬에 맞추어 노래를 부르고 악기를 연주함.",
        };
        Assert.Empty(NarrativeQuality.SimilarGroups(texts));
    }

    [Fact]
    public void 여러_명_중_유사한_둘만_묶인다()
    {
        var texts = new[]
        {
            "곱셈의 원리를 이해하고 두 자리 수 곱셈을 능숙하게 해결함.",
            "곱셈의 원리를 이해하고 두 자리 수 곱셈을 능숙하게 해결함.",   // 0과 동일
            "물의 상태 변화를 관찰하고 그 특징을 설명함.",
        };
        var g = Assert.Single(NarrativeQuality.SimilarGroups(texts));
        Assert.Contains(0, g);
        Assert.Contains(1, g);
        Assert.DoesNotContain(2, g);
    }

    [Fact]
    public void 임계값을_높이면_부분_유사는_안_묶인다()
    {
        var texts = new[]
        {
            "수업에 적극적으로 참여하고 자신의 생각을 명확하게 발표함.",
            "수업에 적극적으로 참여하고 모둠 활동에 성실히 임함.",   // 앞부분만 공유
        };
        Assert.Empty(NarrativeQuality.SimilarGroups(texts, threshold: 0.9));
    }
}
