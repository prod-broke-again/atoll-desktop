using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.App;

/// <summary>
/// Starts a non-visual hosted service a few seconds after launch so SSH/Telegram/etc.
/// do not compete with WebView2 and Explorer during logon.
/// </summary>
public sealed class DeferredHostedService : IHostedService, IDisposable
{
    private readonly IHostedService _inner;
    private readonly TimeSpan _delay;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task _startTask = Task.CompletedTask;

    public DeferredHostedService(IHostedService inner, TimeSpan delay, ILogger logger)
    {
        _inner = inner;
        _delay = delay;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_delay, _cts.Token).ConfigureAwait(false);
                await _inner.StartAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deferred start of {Service} failed", _inner.GetType().Name);
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        try { await _startTask.ConfigureAwait(false); } catch { }
        await _inner.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _cts.Dispose();
}
