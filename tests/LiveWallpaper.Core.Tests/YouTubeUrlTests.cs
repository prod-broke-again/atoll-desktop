using LiveWallpaper.Core.Wallpaper;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class YouTubeUrlTests
{
    [Theory]
    [InlineData("sMUaGTe5Hcg", "sMUaGTe5Hcg")]
    [InlineData("https://www.youtube.com/watch?v=sMUaGTe5Hcg&list=RDsMUaGTe5Hcg&start_radio=1", "sMUaGTe5Hcg")]
    [InlineData("https://youtu.be/sMUaGTe5Hcg", "sMUaGTe5Hcg")]
    [InlineData("youtube.com/embed/sMUaGTe5Hcg", "sMUaGTe5Hcg")]
    [InlineData("https://www.youtube.com/shorts/sMUaGTe5Hcg", "sMUaGTe5Hcg")]
    public void TryGetVideoId_ParsesCommonForms(string input, string expected)
    {
        Assert.True(YouTubeUrl.TryGetVideoId(input, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/watch?v=sMUaGTe5Hcg")]
    [InlineData("hello")]
    public void TryGetVideoId_RejectsUnknownInput(string input)
    {
        Assert.False(YouTubeUrl.TryGetVideoId(input, out _));
    }
}
