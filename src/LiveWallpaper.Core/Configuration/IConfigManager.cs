using System;

namespace LiveWallpaper.Core.Configuration;

public interface IConfigManager
{
    AppConfig CurrentConfig { get; }
    string ConfigFilePath { get; }
    event EventHandler<AppConfig>? ConfigChanged;
    AppConfig Load();
    void Save(AppConfig config);
}
