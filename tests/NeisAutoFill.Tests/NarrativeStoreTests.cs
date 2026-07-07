using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

public class NarrativeStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"narr_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Persists_across_reload()
    {
        var store = new NarrativeStore(_path);
        store.Set("국어", "1", "김다예", "성실하게 참여함.");
        store.Set("수학", "1", "김다예", "연산이 정확함.");

        var reloaded = new NarrativeStore(_path);
        Assert.Equal("성실하게 참여함.", reloaded.Get("국어", "1", "김다예"));
        Assert.Equal("연산이 정확함.", reloaded.Get("수학", "1", "김다예"));
        Assert.Null(reloaded.Get("국어", "2", "박서연"));
    }

    [Fact]
    public void Empty_text_removes_entry()
    {
        var store = new NarrativeStore(_path);
        store.Set("국어", "1", "김다예", "임시 서술문");
        store.Set("국어", "1", "김다예", "");

        Assert.Null(new NarrativeStore(_path).Get("국어", "1", "김다예"));
    }
}
