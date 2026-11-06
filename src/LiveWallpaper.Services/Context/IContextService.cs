using System;
using LiveWallpaper.Core.Context;

namespace LiveWallpaper.Services.Context;

public sealed class ContextModeChangedEventArgs : EventArgs
{
    public string SelectedMode { get; }
    public string EffectiveMode { get; }
    public string Reason { get; }
    public ModeWidgetsConfig Widgets { get; }

    public ContextModeChangedEventArgs(string selectedMode, string effectiveMode, string reason, ModeWidgetsConfig widgets)
    {
        SelectedMode = selectedMode;
        EffectiveMode = effectiveMode;
        Reason = reason;
        Widgets = widgets;
    }
}

public interface IContextService
{
    string SelectedMode { get; }
    string EffectiveMode { get; }
    string CurrentReason { get; }
    ModeWidgetsConfig CurrentWidgets { get; }

    event EventHandler<ContextModeChangedEventArgs>? ModeChanged;

    void SetMode(string mode);
}
