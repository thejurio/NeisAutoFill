using NeisAutoFill.Automation;
using Xunit;

namespace NeisAutoFill.Tests;

// NeisSelectors 정규식을 읽으므로, 그 정적 상태를 바꾸는 원격 테스트와 같은 컬렉션 → 병렬 실행 방지
[Collection("NeisSelectors")]
public class RowMetaParseTests
{
    [Fact]
    public void Parses_no_name_area_from_aria_labels()
    {
        var meta = RowMapBuilder.Parse(
            "3행 반/번호 3",
            "3행 성명 김다예 link",
            "3행 영역 듣기·말하기");

        Assert.Equal("3", meta.No);
        Assert.Equal("김다예", meta.Name);
        Assert.Equal("듣기·말하기", meta.Area);   // 가운뎃점(U+00B7) 보존
    }

    [Fact]
    public void Overlay_row_without_area_yields_null_area()
    {
        // 병합 오버레이 행: 영역 aria-label 이 규칙에 안 맞으면 area=null → 지도 제외 대상
        var meta = RowMapBuilder.Parse("1행 반/번호 1", "1행 성명 홍길동 link", "");
        Assert.Null(meta.Area);
    }

    [Fact]
    public void Handles_multi_char_number_and_spacing()
    {
        var meta = RowMapBuilder.Parse(
            "12행 반/번호 12",
            "12행 성명 남궁민수 link",
            "12행 영역 문법");
        Assert.Equal("12", meta.No);
        Assert.Equal("남궁민수", meta.Name);
        Assert.Equal("문법", meta.Area);
    }

    [Fact]
    public void Parses_last_row_with_extra_token()
    {
        // ★ §8 진짜 원인 회귀 테스트: 마지막 행은 '마지막 행' 토큰이 라벨에 추가된다
        //   (2026-07-07 실기기 덤프: "17행 마지막 행 성명 박서연 (전입학) link")
        var meta = RowMapBuilder.Parse(
            "51행 마지막 행 반/번호 17",
            "51행 마지막 행 성명 박서연 (전입학) link",
            "51행 마지막 행 영역 읽기");

        Assert.Equal("17", meta.No);
        Assert.Equal("박서연", meta.Name);   // (\S+) 라 공백 앞까지 — 정규화와 일관
        Assert.Equal("읽기", meta.Area);
    }

    [Fact]
    public void Last_row_number_without_ban_prefix_also_parses()
    {
        // 종합의견 화면 형식: "N행 마지막 행 번호 17 link"
        var m = NarrativeSelectors.NoFlexRegex().Match("17행 마지막 행 번호 17 link");
        Assert.True(m.Success);
        Assert.Equal("17", m.Groups[1].Value);
    }
}
