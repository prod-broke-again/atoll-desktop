using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Services.Pause;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Server;

public sealed class SshStreamService : BackgroundService, ISshStreamService
{
    private sealed class ChannelContext
    {
        public string Name { get; }
        public string Script { get; }
        public string State { get; set; } = "offline";
        public DateTimeOffset StateChangedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastLineAt { get; set; } = DateTimeOffset.MinValue;
        public bool HasLoggedFirstPoint { get; set; }
        public Process? Process { get; set; }
        public CancellationTokenSource? Cts { get; set; }

        public ChannelContext(string name, string script)
        {
            Name = name;
            Script = script;
        }
    }

    private readonly IConfigManager _configManager;
    private readonly IAutoPauseService _autoPauseService;
    private readonly ILogger<SshStreamService> _logger;
    private readonly ConcurrentDictionary<string, ChannelContext> _channels = new();
    private bool _isDisposed;

    public event Action<string, string>? ChannelStateChanged;
    public event Action<string, string>? LineReceived;

    public SshStreamService(
        IConfigManager configManager,
        IAutoPauseService autoPauseService,
        ILogger<SshStreamService> logger)
    {
        _configManager = configManager;
        _autoPauseService = autoPauseService;
        _logger = logger;

        _autoPauseService.PauseStateChanged += OnPauseStateChanged;
    }

    public string GetChannelState(string channelName)
    {
        return _channels.TryGetValue(channelName, out var ctx) ? ctx.State : "offline";
    }

    public DateTimeOffset? GetLastStateChangeTime(string channelName)
    {
        return _channels.TryGetValue(channelName, out var ctx) ? ctx.StateChangedAt : null;
    }

    public void StartChannel(string channelName, string script)
    {
        if (_isDisposed) return;

        var ctx = _channels.GetOrAdd(channelName, name => new ChannelContext(name, script));
        if (ctx.Process != null && !ctx.Process.HasExited)
        {
            return;
        }

        if (!_autoPauseService.IsPaused && _configManager.CurrentConfig.Server.Enabled)
        {
            StartChannelProcess(ctx);
        }
    }

    public void StopChannel(string channelName)
    {
        if (_channels.TryGetValue(channelName, out var ctx))
        {
            StopChannelProcess(ctx);
            SetChannelState(ctx, "offline");
        }
    }

    public void RestartAll()
    {
        foreach (var ctx in _channels.Values)
        {
            StopChannelProcess(ctx);
            if (!_autoPauseService.IsPaused && _configManager.CurrentConfig.Server.Enabled)
            {
                StartChannelProcess(ctx);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Periodic monitor for stale channels (no data > 10 seconds)
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                foreach (var ctx in _channels.Values)
                {
                    if (ctx.State == "live" && now - ctx.LastLineAt > TimeSpan.FromSeconds(10))
                    {
                        SetChannelState(ctx, "stale");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in SSH channel monitor loop");
            }
        }

        foreach (var ctx in _channels.Values)
        {
            StopChannelProcess(ctx);
        }
    }

    private void OnPauseStateChanged(object? sender, PauseStateEventArgs e)
    {
        if (e.IsPaused)
        {
            _logger.LogInformation("Wallpaper paused: stopping SSH processes.");
            foreach (var ctx in _channels.Values)
            {
                StopChannelProcess(ctx);
                SetChannelState(ctx, "offline");
            }
        }
        else
        {
            _logger.LogInformation("Wallpaper resumed: restarting SSH processes.");
            foreach (var ctx in _channels.Values)
            {
                if (_configManager.CurrentConfig.Server.Enabled)
                {
                    StartChannelProcess(ctx);
                }
            }
        }
    }

    private void StartChannelProcess(ChannelContext ctx)
    {
        StopChannelProcess(ctx);

        var cts = new CancellationTokenSource();
        ctx.Cts = cts;
        ctx.HasLoggedFirstPoint = false;

        _ = Task.Run(() => RunChannelLoopAsync(ctx, cts.Token));
    }

    private async Task RunChannelLoopAsync(ChannelContext ctx, CancellationToken token)
    {
        double backoffSec = 2.0;

        while (!token.IsCancellationRequested && !_isDisposed && !_autoPauseService.IsPaused)
        {
            string alias = _configManager.CurrentConfig.Server.SshAlias;
            if (string.IsNullOrWhiteSpace(alias)) alias = "myserver";

            SetChannelState(ctx, "connecting");

            Process? process = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ssh.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // Keep stdin open so we can close it on stop → remote `cat` gets EOF
                    // and the metrics loop exits instead of becoming an orphan.
                    RedirectStandardInput = true,
                };

                // CRITICAL: Inherit full environment so ProgramData/SystemRoot are preserved
                foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                {
                    if (entry.Key is string k && entry.Value is string v && !string.IsNullOrEmpty(k))
                    {
                        psi.Environment[k] = v;
                    }
                }

                psi.ArgumentList.Add("-T");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveInterval=15");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ServerAliveCountMax=3");
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
                psi.ArgumentList.Add(alias);
                // Ship the script base64-encoded to a temporary file on the remote server and execute it.
                // Critical: executing the script directly with sh keeps stdin free for `cat >/dev/null`
                // to detect when the local SSH session disconnects (stdin EOF) and reap the metrics loop.
                var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ctx.Script.Replace("\r\n", "\n")));
                psi.ArgumentList.Add($"S=\"/tmp/lw2-metrics-${{USER:-$UID}}.sh\"; echo {b64} | (base64 -d 2>/dev/null || base64 -D) > \"$S\" && sh \"$S\" lw2-wallpaper-metrics");

                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                ctx.Process = process;

                process.Start();
                _logger.LogInformation("SSH channel {Channel} process started for alias {Alias} (PID: {Pid}).", ctx.Name, alias, process.Id);

                // Surface remote errors (bad script, auth problems) instead of failing silently.
                var stderrLines = 0;
                process.ErrorDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data) || stderrLines >= 5) return;
                    stderrLines++;
                    _logger.LogWarning("SSH channel {Channel} stderr: {Line}", ctx.Name, e.Data.Length > 300 ? e.Data[..300] : e.Data);
                };
                process.BeginErrorReadLine();

                using var reg = token.Register(() =>
                {
                    try { process.StandardInput.Close(); } catch { }
                    try
                    {
                        if (!process.WaitForExit(500))
                            process.Kill(entireProcessTree: true);
                    }
                    catch { }
                });

                // Read stdout line by line
                using var reader = process.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync(token).ConfigureAwait(false)) != null)
                {
                    if (token.IsCancellationRequested) break;

                    if (!ctx.HasLoggedFirstPoint)
                    {
                        ctx.HasLoggedFirstPoint = true;
                        _logger.LogInformation("SSH channel {Channel} connected and received first data point.", ctx.Name);
                    }

                    ctx.LastLineAt = DateTimeOffset.UtcNow;
                    if (ctx.State != "live")
                    {
                        SetChannelState(ctx, "live");
                    }

                    // Reset backoff upon receiving valid stream data
                    backoffSec = 2.0;

                    try
                    {
                        LineReceived?.Invoke(ctx.Name, line);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing SSH line for channel {Channel}", ctx.Name);
                    }
                }

                await process.WaitForExitAsync(token).ConfigureAwait(false);
                _logger.LogWarning("SSH channel {Channel} process exited with code {Code}.", ctx.Name, process.ExitCode);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SSH channel {Channel} process encountered error", ctx.Name);
            }
            finally
            {
                if (process != null)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            try { process.StandardInput.Close(); } catch { }
                            if (!process.WaitForExit(400))
                                process.Kill(entireProcessTree: true);
                        }
                    }
                    catch { }
                    try { process.Dispose(); } catch { }
                }
                if (ReferenceEquals(ctx.Process, process))
                    ctx.Process = null;
            }

            if (token.IsCancellationRequested || _autoPauseService.IsPaused)
            {
                SetChannelState(ctx, "offline");
                break;
            }

            SetChannelState(ctx, "offline");

            // Exponential backoff with jitter (2s -> 4s -> ... -> 60s max)
            double jitter = Random.Shared.NextDouble() * 1.5;
            double delay = Math.Min(60.0, backoffSec) + jitter;
            _logger.LogInformation("SSH channel {Channel} reconnecting in {Delay:F1}s...", ctx.Name, delay);

            backoffSec = Math.Min(60.0, backoffSec * 2.0);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SetChannelState(ctx, "offline");
    }

    private static void StopChannelProcess(ChannelContext ctx)
    {
        try
        {
            ctx.Cts?.Cancel();
            ctx.Cts?.Dispose();
            ctx.Cts = null;
        }
        catch { }

        var process = ctx.Process;
        if (process == null) return;

        try
        {
            // Closing stdin makes the remote `cat >/dev/null` see EOF and run cleanup,
            // which kills the metrics loop — preferable to leaving orphans after Kill.
            if (!process.HasExited)
            {
                try { process.StandardInput.Close(); } catch { }

                if (!process.WaitForExit(800))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(500);
                }
            }
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        }
        finally
        {
            try { process.Dispose(); } catch { }
            ctx.Process = null;
        }
    }

    private void SetChannelState(ChannelContext ctx, string newState)
    {
        if (ctx.State != newState)
        {
            ctx.State = newState;
            ctx.StateChangedAt = DateTimeOffset.UtcNow;
            ChannelStateChanged?.Invoke(ctx.Name, newState);
        }
    }

    public override void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _autoPauseService.PauseStateChanged -= OnPauseStateChanged;

        foreach (var ctx in _channels.Values)
        {
            StopChannelProcess(ctx);
        }
        _channels.Clear();

        base.Dispose();
    }
}
