using System;
using System.Collections.Generic;
using NeisAutoFill.Core;
using NeisAutoFill.Core.Models;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F2 — AI/엑셀 인식 결과 검수(오인식·누락 감지).</summary>
public class PlanAuditTests
{
    private static readonly string[] Labels = { "잘함", "보통", "노력요함" };

    private static SubjectPlan Plan(string subject, string domain, params (string grade, string text)[] crit)
    {
        var dict = new Dictionary<(string, string), CriteriaEntry>();
        foreach (var (g, t) in crit) dict[(domain, g)] = new CriteriaEntry(t, null);
        return new SubjectPlan(subject, new[] { domain }, dict);
    }

    [Fact]
    public void 완전한_과목은_경고가_없다()
    {
        var plans = new[] { Plan("국어", "듣기",
            ("잘함", "매체를 활용해 발표한다"), ("보통", "내용을 구성해 발표한다"), ("노력요함", "제한적으로 발표한다")) };
        Assert.Empty(PlanAudit.Analyze(plans, Labels));
    }

    [Fact]
    public void 한_등급_기준이_없으면_경고()
    {
        var plans = new[] { Plan("국어", "듣기",
            ("잘함", "매체를 활용해 발표한다"), ("보통", "내용을 구성해 발표한다")) };   // 노력요함 없음
        var warn = Assert.Single(PlanAudit.Analyze(plans, Labels));
        Assert.Equal(PlanWarningLevel.Warn, warn.Level);
        Assert.Equal("듣기", warn.Domain);
        Assert.Contains("노력요함", warn.Message);
    }

    [Fact]
    public void 영역에_기준이_하나도_없으면_경고()
    {
        var plans = new[] { new SubjectPlan("수학", new[] { "수와 연산" },
            new Dictionary<(string, string), CriteriaEntry>()) };
        var warn = Assert.Single(PlanAudit.Analyze(plans, Labels));
        Assert.Contains("하나도", warn.Message);
    }

    [Fact]
    public void 영역이_없으면_과목단위_경고()
    {
        var plans = new[] { new SubjectPlan("체육", Array.Empty<string>(),
            new Dictionary<(string, string), CriteriaEntry>()) };
        var warn = Assert.Single(PlanAudit.Analyze(plans, Labels));
        Assert.Null(warn.Domain);
        Assert.Contains("평가영역이 없", warn.Message);
    }

    [Fact]
    public void 짧은_기준문구는_정보경고()
    {
        var plans = new[] { Plan("국어", "듣기",
            ("잘함", "상"), ("보통", "내용을 구성해 발표한다"), ("노력요함", "제한적으로 발표한다")) };
        var info = Assert.Single(PlanAudit.Analyze(plans, Labels));
        Assert.Equal(PlanWarningLevel.Info, info.Level);
        Assert.Contains("짧아", info.Message);
    }

    [Fact]
    public void WarnCount_는_Warn만_센다()
    {
        // 노력요함 없음(Warn 1) + 잘함·보통 짧음(Info 2)
        var plans = new[] { Plan("국어", "듣기", ("잘함", "상"), ("보통", "중")) };
        var w = PlanAudit.Analyze(plans, Labels);
        Assert.Equal(1, PlanAudit.WarnCount(w));
    }
}
