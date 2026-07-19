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

    // ── F9 M6: 학년·반 조회조건 콤보 찾기 ────────────────────────
    [Fact]
    public void FindQueryCombo_학년_라벨을_키로_찾고_값반환()
    {
        var labels = new string?[] { "학년도, 2026", "학년, 5", "학기, 2", "반, 1", "교과, 국어" };
        var (idx, value) = FindQueryCombo(labels, "학년");
        Assert.Equal(1, idx);
        Assert.Equal("5", value);
    }

    [Fact]
    public void FindQueryCombo_반_라벨을_찾는다()
    {
        var labels = new string?[] { "학년도, 2026", "학년, 5", "반, 3", "교과, 수학" };
        var (idx, value) = FindQueryCombo(labels, "반");
        Assert.Equal(2, idx);
        Assert.Equal("3", value);
    }

    [Fact]
    public void FindQueryCombo_학년은_학년도와_안헷갈린다()
    {
        // "학년도"가 "학년" 앞에 와도 정확히 키 일치(부분일치 아님)해야 한다
        var labels = new string?[] { "학년도, 2026", "학년, 4" };
        var (idx, value) = FindQueryCombo(labels, "학년");
        Assert.Equal(1, idx);
        Assert.Equal("4", value);
    }

    [Fact]
    public void FindQueryCombo_없으면_음수()
    {
        var labels = new string?[] { "학년도, 2026", "교과, 국어", null };
        var (idx, value) = FindQueryCombo(labels, "반");
        Assert.Equal(-1, idx);
        Assert.Null(value);
    }

    [Fact]
    public void FindQueryCombo_학년_라벨이_둘이면_prefer로_진짜학년()
    {
        // 실측 버그: 교과별 평가 화면에 "학년" 라벨이 둘 — 학년도(2026)와 진짜 학년(5)
        var labels = new string?[] { "학년, 2026", "학기, 1", "학년, 5", "반, 1", "교과, 국어" };

        // prefer 없으면 첫 번째(=학년도 2026)를 잡는 문제
        Assert.Equal("2026", FindQueryCombo(labels, "학년").Value);

        // 값이 1~6 인 걸 선호하면 진짜 학년(5)을 잡는다
        var (idx, value) = FindQueryCombo(labels, "학년",
            v => int.TryParse(v, out var g) && g is >= 1 and <= 6);
        Assert.Equal(2, idx);
        Assert.Equal("5", value);
    }
}
