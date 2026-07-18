using System.Collections.Generic;
using NeisAutoFill.Automation;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F14 — 원격 셀렉터 오버라이드 적용·검증. (정적 상태라 순차 실행 + 복원)</summary>
[Collection("NeisSelectors")]   // 정적 상태 공유 — 병렬 실행 방지
public class NeisSelectorsRemoteTests
{
    [Fact]
    public void 유효한_셀렉터는_적용된다()
    {
        var orig = NeisSelectors.Grid;
        try
        {
            var n = NeisSelectors.ApplyRemote(new Dictionary<string, string>
            {
                [nameof(NeisSelectors.Grid)] = "div.new-grid[role='grid']",
            });
            Assert.Equal(1, n);
            Assert.Equal("div.new-grid[role='grid']", NeisSelectors.Grid);
        }
        finally { NeisSelectors.ApplyRemote(new Dictionary<string, string> { [nameof(NeisSelectors.Grid)] = orig }); }
    }

    [Fact]
    public void 빈값과_알수없는키는_무시된다()
    {
        var orig = NeisSelectors.SubjectCombo;
        var n = NeisSelectors.ApplyRemote(new Dictionary<string, string>
        {
            [nameof(NeisSelectors.SubjectCombo)] = "   ",   // 빈값 → 무시
            ["알수없는키"] = "값",                            // 미지 키 → 무시
        });
        Assert.Equal(0, n);
        Assert.Equal(orig, NeisSelectors.SubjectCombo);   // 기존값 유지
    }

    [Fact]
    public void 잘못된_정규식은_무시하고_기존값_유지()
    {
        var orig = NeisSelectors.NoRegex.ToString();
        var n = NeisSelectors.ApplyRemote(new Dictionary<string, string>
        {
            [nameof(NeisSelectors.NoRegex)] = "([",   // 컴파일 불가
        });
        Assert.Equal(0, n);
        Assert.Equal(orig, NeisSelectors.NoRegex.ToString());   // 기존 정규식 유지
    }

    [Fact]
    public void 캡처그룹_없는_정규식은_거부()
    {
        var orig = NeisSelectors.AreaRegex.ToString();
        var n = NeisSelectors.ApplyRemote(new Dictionary<string, string>
        {
            [nameof(NeisSelectors.AreaRegex)] = @"^\d+행 영역",   // 캡처 그룹 없음 → 파싱 결과 못 뽑음
        });
        Assert.Equal(0, n);
        Assert.Equal(orig, NeisSelectors.AreaRegex.ToString());
    }

    [Fact]
    public void 유효한_정규식은_적용되고_동작한다()
    {
        var orig = NeisSelectors.NameRegex.ToString();
        try
        {
            NeisSelectors.ApplyRemote(new Dictionary<string, string>
            {
                [nameof(NeisSelectors.NameRegex)] = @"성명:\s*(\S+)",
            });
            var m = NeisSelectors.NameRegex.Match("성명: 김하늘");
            Assert.True(m.Success);
            Assert.Equal("김하늘", m.Groups[1].Value);
        }
        finally { NeisSelectors.ApplyRemote(new Dictionary<string, string> { [nameof(NeisSelectors.NameRegex)] = orig }); }
    }

    [Fact]
    public void DialogYesNames_는_쉼표로_분리()
    {
        var orig = string.Join(",", NeisSelectors.DialogYesNames);
        try
        {
            NeisSelectors.ApplyRemote(new Dictionary<string, string>
            {
                [nameof(NeisSelectors.DialogYesNames)] = "확인, OK, 예",
            });
            Assert.Equal(new[] { "확인", "OK", "예" }, NeisSelectors.DialogYesNames);
        }
        finally { NeisSelectors.ApplyRemote(new Dictionary<string, string> { [nameof(NeisSelectors.DialogYesNames)] = orig }); }
    }
}
