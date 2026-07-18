using NeisAutoFill.Core;
using Xunit;
using static NeisAutoFill.Core.SubjectComboClassifier;

namespace NeisAutoFill.Tests;

/// <summary>P5 — 과목 콤보 분류(정상 '교과' 우선 · 라벨 버그 폴백).</summary>
public class SubjectComboClassifierTests
{
    [Theory]
    [InlineData("교과, 국어", Kind.Subject, "국어")]
    [InlineData("교과, 수학", Kind.Subject, "수학")]
    public void 정상_교과_라벨은_Subject(string label, Kind kind, string value)
    {
        var (k, v) = Classify(label);
        Assert.Equal(kind, k);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData("학년도, 2026")]
    [InlineData("학기, 2")]
    [InlineData("반, 3")]
    public void 조회조건_숫자값은_과목_아님(string label)
    {
        Assert.Equal(Kind.NotACombo, Classify(label).Kind);
    }

    [Theory]
    [InlineData("학기, 국어", "국어")]   // 종합의견 화면 라벨 버그 — 값이 과목명
    [InlineData("학년, 즐거운 생활", "즐거운 생활")]
    public void 조회조건인데_값이_비숫자면_폴백후보(string label, string value)
    {
        var (k, v) = Classify(label);
        Assert.Equal(Kind.QueryConditionCandidate, k);
        Assert.Equal(value, v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("콤마없는라벨")]
    public void 형식_안맞으면_NotACombo(string? label)
    {
        Assert.Equal(Kind.NotACombo, Classify(label).Kind);
    }

    [Fact]
    public void Pick_정상_교과가_있으면_그것을_폴백없이()
    {
        var labels = new string?[] { "학년도, 2026", "교과, 국어", "학기, 2" };
        var (idx, value, usedFallback) = Pick(labels);
        Assert.Equal(1, idx);
        Assert.Equal("국어", value);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Pick_교과가_없으면_폴백으로_비숫자_조회콤보()
    {
        // 종합의견 화면: 라벨이 전부 "학기, …" 로 붙고 값이 과목명
        var labels = new string?[] { "학년도, 2026", "학기, 국어", "반, 3" };
        var (idx, value, usedFallback) = Pick(labels);
        Assert.Equal(1, idx);
        Assert.Equal("국어", value);
        Assert.True(usedFallback);
    }

    [Fact]
    public void Pick_교과가_있으면_폴백후보보다_우선()
    {
        // 폴백 후보가 앞에 있어도 정상 '교과' 라벨이 이긴다
        var labels = new string?[] { "학기, 국어", "교과, 수학" };
        var (idx, value, usedFallback) = Pick(labels);
        Assert.Equal(1, idx);
        Assert.Equal("수학", value);
        Assert.False(usedFallback);
    }

    [Fact]
    public void Pick_아무것도_없으면_Index_음수()
    {
        var labels = new string?[] { "학년도, 2026", "반, 3", null };
        var (idx, _, usedFallback) = Pick(labels);
        Assert.Equal(-1, idx);
        Assert.False(usedFallback);
    }
}
