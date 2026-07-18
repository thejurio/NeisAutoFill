using System.Collections.Generic;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>서술문 학생 이름 불일치 → 사용자 매핑(nameMap)으로 연결 (등급과 동일 방식).</summary>
public class NarrativeMappingTests
{
    private static Dictionary<int, (string? No, string? Name)> Screen(params (string No, string Name)[] rows)
    {
        var m = new Dictionary<int, (string?, string?)>();
        for (int i = 0; i < rows.Length; i++) m[i] = (rows[i].No, rows[i].Name);
        return m;
    }

    private static Dictionary<int, RowMeta> ScreenMeta(params (string No, string Name)[] rows)
    {
        var m = new Dictionary<int, RowMeta>();
        for (int i = 0; i < rows.Length; i++) m[i] = new RowMeta(rows[i].No, rows[i].Name, null);
        return m;
    }

    [Fact]
    public void 이름_다르면_매핑_없이는_건너뛴다()
    {
        var screen = Screen(("1", "김철수"));
        var entries = new[] { new NarrativeEntry("1", "김철쑤", "발표를 잘함") };   // 오타/이체 이름

        var r = NarrativeMatcher.Build(screen, entries);   // 매핑 없음

        Assert.Empty(r.Todo);   // 자동으로는 못 맞춰 건너뜀
        Assert.Contains(r.Skipped, s => s.Name == "김철쑤");
    }

    [Fact]
    public void nameMap으로_화면성명을_내자료에_연결하면_입력대상()
    {
        var screen = Screen(("1", "김철수"));
        var entries = new[] { new NarrativeEntry("1", "김철쑤", "발표를 잘함") };
        var nameMap = new Dictionary<string, string> { ["김철수"] = "김철쑤" };   // 화면 → 내 자료

        var r = NarrativeMatcher.Build(screen, entries, nameMap);

        var item = Assert.Single(r.Todo);
        Assert.Equal("김철쑤", item.Entry.Name);
        Assert.Equal(0, item.RowIndex);
    }

    [Fact]
    public void nameMap_빈값은_명시적_제외()
    {
        var screen = Screen(("1", "김철수"), ("2", "이영희"));
        var entries = new[]
        {
            new NarrativeEntry("1", "김철수", "발표 잘함"),
            new NarrativeEntry("2", "이영희", "성실함"),
        };
        var nameMap = new Dictionary<string, string> { ["김철수"] = "" };   // 이 학생은 입력 안 함

        var r = NarrativeMatcher.Build(screen, entries, nameMap);

        Assert.Single(r.Todo);   // 이영희만
        Assert.DoesNotContain(r.Todo, t => t.Entry.Name == "김철수");
    }

    [Fact]
    public void AnalyzeNarratives_화면에있는데_내자료에없는_학생을_잡는다()
    {
        var screen = ScreenMeta(("1", "김철수"), ("2", "이영희"));
        var issues = MatchAnalyzer.AnalyzeNarratives("국어", "국어", screen, new[] { "이영희" });

        Assert.False(issues.Clean);
        var u = Assert.Single(issues.UnmatchedStudents);
        Assert.Equal("김철수", u.Name);
        Assert.Empty(issues.UnmatchedAreas);   // 영역은 보지 않음
    }

    [Fact]
    public void AnalyzeNarratives_다_맞으면_Clean()
    {
        var screen = ScreenMeta(("1", "김철수"));
        var issues = MatchAnalyzer.AnalyzeNarratives("국어", "국어", screen, new[] { "김철수" });
        Assert.True(issues.Clean);
    }
}
