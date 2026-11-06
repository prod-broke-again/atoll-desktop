using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using LiveWallpaper.Core.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Server;

public sealed class ServerWidgetService : BackgroundService, IServerWidgetService
{
    private const string MetricsChannel = "metrics";
    private const int MaxHistoryPoints = 300; // 10 minutes at 2-second intervals

    /// <summary>
    /// POSIX sh + awk (mawk-compatible). Prints one LW|{json} line every 2 s.
    /// Sent to the server base64-encoded, so quoting never breaks.
    /// <para>
    /// Critical: the loop must die when the SSH session ends. A bare
    /// <c>while :; do …; done</c> survives local <c>ssh.exe</c> kills and
    /// accumulates hundreds of orphans (CPU 30–50%). We (1) reap prior
    /// sessions, (2) run the loop in the background, (3) block on stdin EOF
    /// so disconnect tears everything down.
    /// </para>
    /// </summary>
    public const string DefaultMetricsScript = """
# lw2-wallpaper-metrics
MARKER=lw2-wallpaper-metrics
LOCK=/tmp/lw2-wallpaper-metrics.lock
PIDFILE=/tmp/lw2-wallpaper-metrics.pid

# Reap previous marked sessions (new builds).
for p in $(pgrep -f "$MARKER" 2>/dev/null); do
  [ "$p" != "$$" ] && kill "$p" 2>/dev/null || true
done

# Reap legacy unmarked orphans: no-tty sh/dash whose only job is `sleep 2`
# (the old infinite metrics loop after ssh.exe was killed).
uid=$(id -u)
for sp in $(pgrep -u "$uid" -x sleep 2>/dev/null); do
  args=$(ps -o args= -p "$sp" 2>/dev/null || true)
  case "$args" in
    "sleep 2"|"sleep 2 "*) ;;
    *) continue ;;
  esac
  ppid=$(ps -o ppid= -p "$sp" 2>/dev/null | tr -d ' ')
  [ -n "$ppid" ] && [ "$ppid" -gt 1 ] || continue
  comm=$(ps -o comm= -p "$ppid" 2>/dev/null | tr -d ' ')
  case "$comm" in sh|dash|bash) ;; *) continue ;; esac
  tty=$(ps -o tty= -p "$ppid" 2>/dev/null | tr -d ' ')
  case "$tty" in ""|"?") kill "$ppid" 2>/dev/null || true ;; esac
done

# Pidfile leftover from a prior session.
if [ -f "$PIDFILE" ]; then
  old=$(cat "$PIDFILE" 2>/dev/null || true)
  if [ -n "$old" ] && kill -0 "$old" 2>/dev/null; then
    kill "$old" 2>/dev/null || true
    pkill -P "$old" 2>/dev/null || true
    sleep 0.2
    kill -9 "$old" 2>/dev/null || true
  fi
  rm -f "$PIDFILE"
fi

# Exclusive lock so two overlapping SSH sessions never both sample.
if command -v flock >/dev/null 2>&1; then
  exec 9>"$LOCK"
  if ! flock -n 9; then
    fuser -k "$LOCK" >/dev/null 2>&1 || true
    sleep 0.2
    flock -n 9 || exit 0
  fi
fi

cleanup() {
  [ -n "${MPID:-}" ] && kill "$MPID" 2>/dev/null
  [ -n "${MPID:-}" ] && pkill -P "$MPID" 2>/dev/null
  rm -f "$PIDFILE" "/tmp/lw2-metrics-${USER:-$UID}.sh" 2>/dev/null || true
  exit 0
}
trap cleanup EXIT HUP INT TERM

IF=$(awk '$2=="00000000"{print $1; exit}' /proc/net/route)
(
while :; do
T=$(date +%s)
D=$(df -B1 / | awk 'NR==2{print $2" "$3}')
awk -v t="$T" -v ifc="$IF" -v d="$D" '
FILENAME=="/proc/stat" && $1=="cpu" {cpu=$2","$3","$4","$5","$6","$7","$8","$9}
FILENAME=="/proc/meminfo" {if($1=="MemTotal:")mt=$2; else if($1=="MemAvailable:")ma=$2; else if($1=="SwapTotal:")st=$2; else if($1=="SwapFree:")sf=$2}
FILENAME=="/proc/loadavg" {l1=$1;l5=$2;l15=$3}
FILENAME=="/proc/uptime" {up=int($1)}
FILENAME=="/proc/net/dev" {line=$0; sub(/^ +/,"",line); n=split(line,a,/[: ]+/); if(a[1]==ifc){rx=a[2];tx=a[10]}}
END{split(d,dd," "); if(rx=="")rx=0; if(tx=="")tx=0;
printf "LW|{\"t\":%s,\"cpu\":[%s],\"mem\":{\"total\":%.0f,\"avail\":%.0f},\"swap\":{\"total\":%.0f,\"free\":%.0f},\"load\":[%s,%s,%s],\"disk\":{\"total\":%s,\"used\":%s},\"net\":{\"rx\":%s,\"tx\":%s},\"up\":%s}\n",t,cpu,mt*1024,ma*1024,st*1024,sf*1024,l1,l5,l15,dd[1],dd[2],rx,tx,up}' /proc/stat /proc/meminfo /proc/loadavg /proc/uptime /proc/net/dev || exit
sleep 2
done
) &
MPID=$!
echo "$MPID" >"$PIDFILE"

# Block until SSH disconnects (stdin EOF) — then trap cleanup kills the loop.
cat >/dev/null
cleanup
""";

    private readonly ISshStreamService _sshStream;
    private readonly ITimewebClient _timewebClient;
    private readonly ISiteHealthService _siteHealth;
    private readonly IConfigManager _configManager;
    private readonly ILogger<ServerWidgetService> _logger;

    private readonly object _lock = new();
    private readonly List<MetricPoint> _history = new(MaxHistoryPoints);
    private RawServerMetrics? _lastRaw;

    private TimewebSnapshot _timewebSnapshot = new();
    private ServerStateSnapshot _currentSnapshot = new();
    private string _lastSshAlias = string.Empty;

    public event Action<MetricPoint>? MetricPointReceived;
    public event Action<ServerStateSnapshot>? StateSnapshotUpdated;

    public ServerStateSnapshot CurrentSnapshot
    {
        get
        {
            lock (_lock) return _currentSnapshot;
        }
    }

    public IReadOnlyList<MetricPoint> History
    {
        get
        {
            lock (_lock) return _history.ToList();
        }
    }

    public ServerWidgetService(
        ISshStreamService sshStream,
        ITimewebClient timewebClient,
        ISiteHealthService siteHealth,
        IConfigManager configManager,
        ILogger<ServerWidgetService> logger)
    {
        _sshStream = sshStream;
        _timewebClient = timewebClient;
        _siteHealth = siteHealth;
        _configManager = configManager;
        _logger = logger;

        _sshStream.LineReceived += OnSshLineReceived;
        _sshStream.ChannelStateChanged += OnSshChannelStateChanged;
        _configManager.ConfigChanged += OnConfigChanged;
    }

    public void SetPrivacyMode(bool enabled)
    {
        lock (_lock)
        {
            _currentSnapshot = _currentSnapshot with { PrivacyMode = enabled };
        }
        StateSnapshotUpdated?.Invoke(CurrentSnapshot);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_configManager.CurrentConfig.Server.Enabled)
        {
            _sshStream.StartChannel(MetricsChannel, DefaultMetricsScript);
        }

        // Initial quick site check
        _ = Task.Run(async () =>
        {
            try
            {
                await _siteHealth.CheckAllAsync(stoppingToken).ConfigureAwait(false);
                UpdateSnapshotAndAlerts();
            }
            catch { }
        }, stoppingToken);

        var nextSiteCheck = DateTimeOffset.UtcNow;
        var nextTimewebCheck = DateTimeOffset.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, stoppingToken).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;

                // Timeweb polling (every 10 seconds if enabled)
                if (_configManager.CurrentConfig.Server.Enabled && _configManager.CurrentConfig.Server.TimewebEnabled && now >= nextTimewebCheck)
                {
                    _timewebSnapshot = await _timewebClient.PollAsync(stoppingToken).ConfigureAwait(false);
                    nextTimewebCheck = now.AddSeconds(10);
                    UpdateSnapshotAndAlerts();
                }

                // Sites health check (every 60s)
                if (_configManager.CurrentConfig.Server.Enabled && now >= nextSiteCheck)
                {
                    await _siteHealth.CheckAllAsync(stoppingToken).ConfigureAwait(false);
                    nextSiteCheck = now.AddSeconds(60);
                    UpdateSnapshotAndAlerts();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in server widget service loop");
            }
        }
    }

    private void OnSshLineReceived(string channel, string line)
    {
        if (channel != MetricsChannel) return;

        if (!ServerMetricsParser.TryParse(line, out var raw) || raw == null)
        {
            return;
        }

        MetricPoint point;
        lock (_lock)
        {
            point = ServerMetricCalculator.Calculate(raw, _lastRaw);
            _lastRaw = raw;

            _history.Add(point);
            if (_history.Count > MaxHistoryPoints)
            {
                _history.RemoveAt(0);
            }
        }

        MetricPointReceived?.Invoke(point);

        // Periodically update current snapshot with latest metric point stats
        UpdateSnapshotAndAlerts();
    }

    private void OnSshChannelStateChanged(string channel, string state)
    {
        if (channel == MetricsChannel)
        {
            UpdateSnapshotAndAlerts();
        }
    }

    private void OnConfigChanged(object? sender, AppConfig cfg)
    {
        var aliasChanged = !string.Equals(_lastSshAlias, cfg.Server.SshAlias, StringComparison.Ordinal);
        _lastSshAlias = cfg.Server.SshAlias;

        if (cfg.Server.Enabled)
        {
            if (aliasChanged)
            {
                _sshStream.StopChannel(MetricsChannel);
            }
            _sshStream.StartChannel(MetricsChannel, DefaultMetricsScript);
        }
        else
        {
            _sshStream.StopChannel(MetricsChannel);
        }

        UpdateSnapshotAndAlerts();
    }

    private void UpdateSnapshotAndAlerts()
    {
        var cfg = _configManager.CurrentConfig;
        string sshState = _sshStream.GetChannelState(MetricsChannel);
        var sshStateChangedAt = _sshStream.GetLastStateChangeTime(MetricsChannel) ?? DateTimeOffset.UtcNow;
        var sshOfflineDuration = sshState == "offline" ? DateTimeOffset.UtcNow - sshStateChangedAt : TimeSpan.Zero;

        bool isNetAlive = NetworkInterface.GetIsNetworkAvailable();

        List<MetricPoint> historyCopy;
        MetricPoint? latestPoint = null;
        RawServerMetrics? latestRaw;
        lock (_lock)
        {
            historyCopy = _history.ToList();
            latestPoint = _history.LastOrDefault();
            latestRaw = _lastRaw;
        }

        var siteRecords = _siteHealth.GetCurrentStatus();
        var siteAlertInputs = siteRecords.Select(s => new SiteAlertInput
        {
            Name = s.Name,
            Url = s.Url,
            State = s.State,
            ResponseMs = s.ResponseMs,
            TlsDays = s.TlsDays,
            ConsecutiveDownCount = s.ConsecutiveDownCount
        }).ToList();

        double? backupAgeHours = null;
        if (_timewebSnapshot.LatestBackup?.CreatedAt.HasValue == true)
        {
            backupAgeHours = (DateTimeOffset.UtcNow - _timewebSnapshot.LatestBackup.CreatedAt.Value).TotalHours;
        }

        double? bucketUsedPct = _timewebSnapshot.Bucket?.ComputeUsedPct();

        var alertInput = new ServerStateInput
        {
            SshState = sshState,
            SshOfflineDuration = sshOfflineDuration,
            IsInternetAlive = isNetAlive,
            ServerStatus = _timewebSnapshot.Server?.Status ?? (sshState == "live" ? "on" : null),
            IsAccountBlocked = _timewebSnapshot.IsAccountBlocked,
            HoursLeft = _timewebSnapshot.Finances?.HoursLeft,
            IsAutopayEnabled = _timewebSnapshot.Finances?.HasAutopay() ?? false,
            CurrentRamPct = latestPoint?.Mem ?? 0.0,
            CurrentDiskPct = latestPoint?.DiskPct ?? (_timewebSnapshot.Server?.ComputeDiskPct() ?? 0.0),
            RecentCpuPoints = historyCopy,
            Sites = siteAlertInputs,
            BackupAgeHours = backupAgeHours,
            BucketUsedPct = bucketUsedPct,
            TimewebStaleMinutes = _timewebSnapshot.StaleMinutes
        };

        var evaluatedAlerts = ServerAlertEvaluator.Evaluate(alertInput);

        // Build server site info list
        var siteInfos = siteRecords.Select(s => new ServerSiteInfo
        {
            Name = s.Name,
            Url = s.Url,
            State = s.State,
            Ms = s.ResponseMs,
            TlsDays = s.TlsDays
        }).ToList();

        // Build server status info
        ServerStatusInfo? serverInfo = null;
        if (_timewebSnapshot.Server != null || latestRaw != null)
        {
            serverInfo = new ServerStatusInfo
            {
                Status = _timewebSnapshot.Server?.Status ?? "on",
                UptimeSec = latestRaw?.Up ?? 0,
                DiskPct = latestPoint?.DiskPct ?? (_timewebSnapshot.Server?.ComputeDiskPct() ?? 0.0)
            };
        }

        // Build account info
        AccountFinanceInfo? accountInfo = null;
        if (_timewebSnapshot.Finances != null)
        {
            accountInfo = new AccountFinanceInfo
            {
                Balance = _timewebSnapshot.Finances.Balance,
                Currency = _timewebSnapshot.Finances.Currency,
                DaysLeft = _timewebSnapshot.Finances.ComputeDaysLeft(),
                Autopay = _timewebSnapshot.Finances.HasAutopay(),
                StaleMin = _timewebSnapshot.StaleMinutes
            };
        }

        // Build backup info
        BackupInfo? backupInfo = null;
        if (backupAgeHours.HasValue)
        {
            backupInfo = new BackupInfo { AgeHours = backupAgeHours.Value };
        }

        // Build bucket info
        BucketInfo? bucketInfo = null;
        if (_timewebSnapshot.Bucket != null)
        {
            bucketInfo = new BucketInfo
            {
                UsedPct = _timewebSnapshot.Bucket.ComputeUsedPct(),
                Objects = _timewebSnapshot.Bucket.ObjectAmount ?? 0
            };
        }

        string hostName = cfg.Server.Sites.FirstOrDefault()?.Name ?? "api.example.com";

        ServerStateSnapshot snapshot;
        lock (_lock)
        {
            snapshot = new ServerStateSnapshot
            {
                Host = hostName,
                Ssh = sshState,
                Server = serverInfo,
                Sites = siteInfos,
                Account = accountInfo,
                Backup = backupInfo,
                Bucket = bucketInfo,
                Alerts = evaluatedAlerts,
                PrivacyMode = cfg.Server.PrivacyMode
            };
            _currentSnapshot = snapshot;
        }

        StateSnapshotUpdated?.Invoke(snapshot);
    }

    public async Task<string> RunDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== ДИАГНОСТИКА ПОДКЛЮЧЕНИЙ ===");

        // 1. SSH test
        string alias = _configManager.CurrentConfig.Server.SshAlias;
        if (string.IsNullOrWhiteSpace(alias)) alias = "myserver";
        sb.Append($"1. SSH ({alias}): ");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string k && entry.Value is string v && !string.IsNullOrEmpty(k))
                {
                    psi.Environment[k] = v;
                }
            }
            psi.ArgumentList.Add("-T");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=5");
            psi.ArgumentList.Add(alias);
            psi.ArgumentList.Add("echo ok");

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var timeoutTask = Task.Delay(5000, cancellationToken);
            var waitTask = proc.WaitForExitAsync(cancellationToken);

            var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
            if (completed == waitTask && proc.ExitCode == 0)
            {
                sb.AppendLine("УСПЕШНО (подключение установлено, ключ и алиас работают)");
            }
            else
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                string err = (await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
                sb.AppendLine($"ОШИБКА (код {proc.ExitCode}: {err})");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ОШИБКА ({ex.Message})");
        }

        // 2. Timeweb test
        sb.Append("2. Timeweb Cloud API: ");
        string twRes = await _timewebClient.CheckConnectionAsync(cancellationToken).ConfigureAwait(false);
        sb.AppendLine(twRes);

        // 3. Sites test
        sb.AppendLine("3. Сайты:");
        var sites = _configManager.CurrentConfig.Server.Sites;
        if (sites.Count == 0)
        {
            sb.AppendLine("   (список сайтов пуст)");
        }
        else
        {
            foreach (var site in sites)
            {
                string res = await _siteHealth.CheckSiteDiagnosticAsync(site.Name, site.Url, cancellationToken).ConfigureAwait(false);
                sb.AppendLine($"   - {res}");
            }
        }

        return sb.ToString();
    }
}
