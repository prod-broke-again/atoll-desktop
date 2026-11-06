using LiveWallpaper.Core.Telegram;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public sealed class TelegramLinksTests
{
    [Theory]
    [InlineData("http://127.0.0.1:18765", "http://127.0.0.1:18765")]
    [InlineData("http://127.0.0.1:18765/", "http://127.0.0.1:18765")]
    [InlineData("http://localhost:18765/api", "http://localhost:18765")]
    [InlineData("http://[::1]:18765", "http://[::1]:18765")]
    public void TryNormalizeBaseUrl_AcceptsLoopback(string raw, string expected)
    {
        Assert.True(TelegramLinks.TryNormalizeBaseUrl(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("http://example.com:18765")]
    [InlineData("http://10.0.0.5:18765")]
    [InlineData("file:///C:/secrets")]
    [InlineData("http://user:pass@127.0.0.1:18765")]
    [InlineData("not a url")]
    public void TryNormalizeBaseUrl_RejectsAnythingElse(string raw)
    {
        Assert.False(TelegramLinks.TryNormalizeBaseUrl(raw, out var normalized));
        Assert.Equal(TelegramLinks.DefaultBaseUrl, normalized);
    }

    [Fact]
    public void TryBuildChatUri_UsesUsernameWhenPresent()
    {
        var uri = TelegramLinks.TryBuildChatUri(-1002969802619, "@DevCommunity", "forum");
        Assert.NotNull(uri);
        Assert.Equal("https://t.me/DevCommunity", uri.AbsoluteUri);
    }

    [Fact]
    public void TryBuildChatUri_UserWithoutUsername_UsesTgScheme()
    {
        var uri = TelegramLinks.TryBuildChatUri(424242, null, "user");
        Assert.NotNull(uri);
        Assert.Equal("tg://openmessage?user_id=424242", TelegramLinks.ToShellTarget(uri));
    }

    [Theory]
    [InlineData(-1002969802619, "forum")]
    [InlineData(-1002969802619, "supergroup")]
    public void TryBuildChatUri_PrivateChannel_OpensByMtProtoId(long id, string chatType)
    {
        var uri = TelegramLinks.TryBuildChatUri(id, null, chatType);
        Assert.NotNull(uri);
        Assert.Equal("tg://privatepost?channel=2969802619&post=1", TelegramLinks.ToShellTarget(uri));

        var invalidUsername = TelegramLinks.TryBuildChatUri(id, "bad name", chatType);
        Assert.NotNull(invalidUsername);
        Assert.Equal("tg://privatepost?channel=2969802619&post=1", TelegramLinks.ToShellTarget(invalidUsername));
    }

    [Theory]
    [InlineData(-4919221831, "group")]
    [InlineData(-100123, "supergroup")]
    [InlineData(-5, "group")]
    [InlineData(10, "channel")]
    public void TryBuildChatUri_DoesNotInventLinksForBasicGroups(long id, string chatType)
    {
        Assert.Null(TelegramLinks.TryBuildChatUri(id, null, chatType));
        Assert.Null(TelegramLinks.TryBuildChatUri(id, "bad name", chatType));
    }
}
