using NeisAutoFill.Core;
using Xunit;

namespace NeisAutoFill.Tests;

/// <summary>F9 — 다중 학급 프로필 경로·이름 규칙.</summary>
public class ProfilePathsTests
{
    [Theory]
    [InlineData("기본", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("3-1 영어", false)]
    public void 기본_프로필_판별(string? profile, bool isDefault)
    {
        Assert.Equal(isDefault, ProfilePaths.IsDefault(profile));
    }

    [Fact]
    public void 기본_프로필은_기존_경로_그대로()
    {
        // 하위호환: 담임(기본)은 하위폴더 없이 기존 경로
        Assert.Equal(@"C:\Docs\NeisAutoFill", ProfilePaths.WorkspaceDir(@"C:\Docs\NeisAutoFill", "기본"));
        Assert.Equal(@"C:\App\narratives.json", ProfilePaths.DataFile(@"C:\App", "기본", "narratives.json"));
    }

    [Fact]
    public void 전담_세트는_하위폴더로_분리()
    {
        Assert.Equal(@"C:\Docs\NeisAutoFill\3-1 영어",
            ProfilePaths.WorkspaceDir(@"C:\Docs\NeisAutoFill", "3-1 영어"));
        Assert.Equal(@"C:\App\profiles\3-1 영어\narratives.json",
            ProfilePaths.DataFile(@"C:\App", "3-1 영어", "narratives.json"));
    }

    [Fact]
    public void 확장성_같은_프로필에_여러_영역_파일()
    {
        // 창체·행발특 확장: 파일명만 바꾸면 같은 프로필 폴더에 공존
        var a = ProfilePaths.DataFile(@"C:\App", "3-1 영어", "narratives.json");
        var b = ProfilePaths.DataFile(@"C:\App", "3-1 영어", "창체.json");
        Assert.Equal(@"C:\App\profiles\3-1 영어", System.IO.Path.GetDirectoryName(a));
        Assert.Equal(System.IO.Path.GetDirectoryName(a), System.IO.Path.GetDirectoryName(b));
    }

    [Theory]
    [InlineData("3-1 영어", true)]
    [InlineData("우리반", true)]
    [InlineData("4학년 2반 과학", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]      // 경로 구분자
    [InlineData("x:y", false)]      // 금지문자
    [InlineData("..", false)]       // 상위 경로
    [InlineData("a*b", false)]
    public void 프로필명_유효성(string? name, bool valid)
    {
        Assert.Equal(valid, ProfilePaths.IsValidName(name));
    }

    [Fact]
    public void 너무_긴_이름은_거부()
    {
        Assert.False(ProfilePaths.IsValidName(new string('가', 41)));
        Assert.True(ProfilePaths.IsValidName(new string('가', 40)));
    }
}
