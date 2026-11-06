using System;

namespace LiveWallpaper.Services.Server;

public interface ISshStreamService
{
    string GetChannelState(string channelName);
    DateTimeOffset? GetLastStateChangeTime(string channelName);
    event Action<string, string>? ChannelStateChanged; // (channelName, state)
    event Action<string, string>? LineReceived; // (channelName, line)
    void StartChannel(string channelName, string script);
    void StopChannel(string channelName);
    void RestartAll();
}
