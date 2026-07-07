using NeisAutoFill.Core.Scale;
using Xunit;

namespace NeisAutoFill.Tests;

public class JsonScaleStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scales_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Builtin_presets_always_present()
    {
        var store = new JsonScaleStore(_path);
        Assert.Contains(store.Presets, s => s.Name == GradePresets.ThreeLevel.Name);
        Assert.Contains(store.Presets, s => s.Name == GradePresets.FiveLevel.Name);
    }

    [Fact]
    public void Cannot_upsert_over_builtin()
    {
        var store = new JsonScaleStore(_path);
        Assert.Throws<InvalidOperationException>(() =>
            store.Upsert(new GradeScale(GradePresets.ThreeLevel.Name,
                new[] { new GradeLevel("x") })));
    }

    [Fact]
    public void User_scale_persists_across_reload()
    {
        var custom = new GradeScale("우리학교4단계", new[]
        {
            new GradeLevel("최상"), new GradeLevel("상"),
            new GradeLevel("중"), new GradeLevel("하"),
        });

        var store = new JsonScaleStore(_path);
        store.Upsert(custom);
        store.Active = custom;
        store.Save();

        var reloaded = new JsonScaleStore(_path);
        Assert.Contains(reloaded.Presets, s => s.Name == "우리학교4단계");
        Assert.Equal("우리학교4단계", reloaded.Active.Name);
        Assert.Equal(4, reloaded.Active.Levels.Count);
    }

    [Fact]
    public void Remove_builtin_returns_false()
    {
        var store = new JsonScaleStore(_path);
        Assert.False(store.Remove(GradePresets.ThreeLevel.Name));
    }

}
