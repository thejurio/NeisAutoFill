using NeisAutoFill.Core;
using NeisAutoFill.Core.Matching;
using NeisAutoFill.Core.Models;
using Xunit;

namespace NeisAutoFill.Tests;

public class NarrativeMatcherTests
{
    private static Dictionary<int, (string?, string?)> Rows(
        params (int idx, string? no, string? name)[] r) =>
        r.ToDictionary(x => x.idx, x => (x.no, x.name));

    [Fact]
    public void Matches_by_number_and_name()
    {
        var rows = Rows((0, "1", "김다예"), (1, "2", "박서연"));
        var entries = new[]
        {
            new NarrativeEntry("1", "김다예", "국어 서술문 A"),
            new NarrativeEntry("2", "박서연(전입학)", "국어 서술문 B"),
        };

        var result = NarrativeMatcher.Build(rows, entries);

        Assert.Equal(2, result.Todo.Count);
        Assert.Empty(result.Skipped);
        Assert.Equal(0, result.Todo[0].RowIndex);
        Assert.Equal("국어 서술문 B", result.Todo[1].Entry.Text);
    }

    [Fact]
    public void Entry_without_screen_row_is_reported_skipped()
    {
        var rows = Rows((0, "1", "김다예"));
        var entries = new[]
        {
            new NarrativeEntry("1", "김다예", "서술문"),
            new NarrativeEntry("9", "없는학생", "서술문"),
        };

        var result = NarrativeMatcher.Build(rows, entries);

        Assert.Single(result.Todo);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal("없는학생", skip.Name);
        Assert.Contains("찾지 못함", skip.Reason);
    }

    [Fact]
    public void Screen_row_without_entry_is_silently_ignored()
    {
        // 서술문 안 만든 학생의 행은 스킵 사유도 아님 — 그냥 지나감
        var rows = Rows((0, "1", "김다예"), (1, "2", "박서연"));
        var entries = new[] { new NarrativeEntry("1", "김다예", "서술문") };

        var result = NarrativeMatcher.Build(rows, entries);

        Assert.Single(result.Todo);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Empty_text_is_skipped_with_reason()
    {
        var rows = Rows((0, "1", "김다예"));
        var entries = new[] { new NarrativeEntry("1", "김다예", "  ") };

        var result = NarrativeMatcher.Build(rows, entries);

        Assert.Empty(result.Todo);
        Assert.Contains("비어 있음", Assert.Single(result.Skipped).Reason);
    }
}

public class TextMetricsTests
{
    [Fact]
    public void Korean_chars_count_3_utf8_bytes()
    {
        Assert.Equal(3, TextMetrics.Utf8Bytes("가"));
        Assert.Equal(6, TextMetrics.Utf8Bytes("가나"));
        Assert.Equal(1, TextMetrics.Utf8Bytes("a"));
    }

    [Fact]
    public void Summary_shows_chars_and_bytes()
    {
        Assert.Equal("2자 / 6바이트", TextMetrics.Summary("가나"));
    }
}
