using NeisAutoFill.Core.Evaluation;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>
/// 같은 영역의 평가들을 <b>속으로만</b> 구별하는 이름 (PlanKeys).
/// 사람과 나이스에게는 언제나 진짜 영역명이 나가야 한다 (사용자 확인 2026-08-22).
/// </summary>
public class PlanKeysTests
{
    [Fact]
    public void 겹치지_않는_영역은_이름을_건드리지_않는다()
    {
        // 지금까지 만든 파일과 그대로 맞아야 한다 — 꼬리가 붙으면 옛 성적이 딴 열로 간다
        Assert.Equal(new[] { "문법", "읽기" }, PlanKeys.Build(new[] { "문법", "읽기" }));
    }

    [Fact]
    public void 겹치는_영역은_두_번째부터_번호를_붙인다()
    {
        Assert.Equal(new[] { "한국사", "한국사#2", "한국사#3" },
            PlanKeys.Build(new[] { "한국사", "한국사", "한국사" }));
    }

    [Fact]
    public void 이미_꼬리가_달린_값이_들어와도_두_번_붙지_않는다()
    {
        // 파일에서 읽은 키를 그대로 다시 넣는 경로가 있다
        Assert.Equal(new[] { "한국사", "한국사#2" }, PlanKeys.Build(new[] { "한국사", "한국사#2" }));
    }

    [Theory]
    [InlineData("한국사#2", "한국사")]
    [InlineData("한국사", "한국사")]
    [InlineData("수와 연산#10", "수와 연산")]
    public void 꼬리를_떼면_진짜_영역명이다(string key, string name) =>
        Assert.Equal(name, PlanKeys.NameOf(key));

    [Theory]
    [InlineData("C#")]            // 꼬리 뒤에 숫자가 없다
    [InlineData("#해시로시작")]     // 앞이 비어 있다
    [InlineData("1#2반 활동")]      // 숫자가 아닌 글자가 섞였다
    public void 영역명에_들어간_샵은_건드리지_않는다(string name) =>
        Assert.Equal(name, PlanKeys.NameOf(name));

    [Fact]
    public void 같은_영역이_두_번_나오는지_알려준다()
    {
        Assert.True(PlanKeys.HasRepeats(new[] { "한국사", "한국사#2" }));
        Assert.False(PlanKeys.HasRepeats(new[] { "문법", "읽기" }));
    }
}
