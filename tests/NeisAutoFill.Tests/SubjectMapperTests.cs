using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>전과목 업로드 과목 매핑(자동 제안) — 순수 로직.</summary>
public class SubjectMapperTests
{
    [Fact]
    public void 정확_일치는_자동_확정()
    {
        var s = SubjectMapper.Suggest(new[] { "국어", "수학" }, new[] { "수학", "국어", "영어" });
        Assert.Equal("국어", s[0].Screen);
        Assert.True(s[0].Auto);
        Assert.Equal("수학", s[1].Screen);
        Assert.True(s[1].Auto);
    }

    [Fact]
    public void 괄호_공백_차이는_정규화로_자동_확정()
    {
        var s = SubjectMapper.Suggest(new[] { "국어", "즐거운 생활" }, new[] { "국어(1학기)", "즐거운생활" });
        Assert.Equal("국어(1학기)", s[0].Screen);
        Assert.True(s[0].Auto);
        Assert.Equal("즐거운생활", s[1].Screen);
        Assert.True(s[1].Auto);
    }

    [Fact]
    public void 화면에_없는_과목은_자동확정_아님()
    {
        var s = SubjectMapper.Suggest(new[] { "한자" }, new[] { "국어", "수학" });
        Assert.False(s[0].Auto);   // 정확·정규화 일치 없음 → 사용자 확인 필요
    }

    [Fact]
    public void 매핑_순서와_개수는_입력과_동일()
    {
        var s = SubjectMapper.Suggest(new[] { "국어", "수학", "영어" }, new[] { "국어" });
        Assert.Equal(3, s.Count);
        Assert.Equal(new[] { "국어", "수학", "영어" }, new[] { s[0].MySubject, s[1].MySubject, s[2].MySubject });
        Assert.True(s[0].Auto);    // 국어만 자동
        Assert.False(s[1].Auto);
    }
}
