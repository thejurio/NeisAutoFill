using NeisAutoFill.Core.Matching;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>R1 — 입력 전 확인 창의 자동 제안 로직.</summary>
public class SimilaritySuggesterTests
{
    [Theory]
    [InlineData("박서연", "박서현")]     // 편집거리 1
    [InlineData("듣기말하기", "듣기·말하기")]  // 중점 무시 일치
    [InlineData("김 하늘", "김하늘")]    // 공백 무시 일치
    public void 비슷한_이름을_제안한다(string screen, string expected)
    {
        var pick = SimilaritySuggester.Suggest(screen, new[] { "이준서", expected, "정다은" });
        Assert.Equal(expected, pick);
    }

    [Fact]
    public void 충분히_비슷한게_없으면_null()
    {
        Assert.Null(SimilaritySuggester.Suggest("최민준", new[] { "김하늘", "이준서" }));
    }

    [Fact]
    public void 영역_배정_이름일치_자동선택_후_남는것_순서제안()
    {
        // 화면: [듣기, 쓰기, 문학] / 엑셀: [듣기, 읽기]
        var r = SimilaritySuggester.AssignAreasByOrder(
            new string?[] { "듣기", "쓰기", "문학" }, 3, new[] { "듣기", "읽기" });

        Assert.Equal(("듣기", true), r[0]);    // 이름 일치 → 자동
        Assert.Equal(("읽기", false), r[1]);   // 남는 영역 순서 제안
        Assert.Equal(((string?)null, false), r[2]);   // 부족 → 제외
    }

    [Fact]
    public void 화면에_같은_영역명이_반복돼도_중복_배정_안함()
    {
        // 화면: [읽기, 읽기] / 엑셀: [읽기, 읽기(2)] — v1.3.0 반복 평가 시나리오
        var r = SimilaritySuggester.AssignAreasByOrder(
            new string?[] { "읽기", "읽기" }, 2, new[] { "읽기", "읽기(2)" });

        Assert.Equal(("읽기", true), r[0]);
        Assert.Equal(("읽기(2)", false), r[1]);
    }

    [Fact]
    public void 엑셀_영역이_더_적으면_뒷줄은_제외()
    {
        var r = SimilaritySuggester.AssignAreasByOrder(
            new string?[] { "a", "b", "c", "d" }, 4, new[] { "a" });
        Assert.Equal(("a", true), r[0]);
        Assert.All(r.Skip(1), x => Assert.Null(x.Area));
    }
}
