using NeisAutoFill.Core.Matching;
using Xunit;

namespace NeisAutoFill.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData("박서연(전입학)", "박서연")]
    [InlineData("김다예", "김다예")]
    [InlineData("이준호 (전출)", "이준호")]
    [InlineData("  홍길동  ", "홍길동")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_removes_paren_suffix_and_trims(string? input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_strips_only_when_paren_at_end()
    {
        // 접미어 위치의 괄호만 대상. 정상 이름은 변형 없음
        Assert.Equal("김철수", NameNormalizer.Normalize("김철수(1반)"));
    }
}
