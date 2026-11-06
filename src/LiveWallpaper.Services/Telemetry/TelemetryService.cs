using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Telemetry;

public interface ITelemetryService
{
    TelemetryData? CurrentTelemetry { get; }
    event EventHandler<TelemetryData>? TelemetryUpdated;
    void SetPaused(bool isPaused);
}

public sealed class TelemetryService : BackgroundService, ITelemetryService
{
    private readonly IConfigManager _configManager;
    private readonly ILogger<TelemetryService> _logger;

    private readonly List<PerformanceCounter> _gpuCounters = new();
    private bool _gpuCountersInitialized;
    private DateTime _lastGpuCounterCheck = DateTime.MinValue;

    private long _prevCpuIdle;
    private long _prevCpuKernel;
    private long _prevCpuUser;
    private DateTime _prevCpuSampleTime = DateTime.MinValue;

    private long _prevBytesReceived;
    private long _prevBytesSent;
    private DateTime _prevNetSampleTime = DateTime.MinValue;

    private System.Threading.Timer? _pollTimer;
    private bool _isPaused;
    private TelemetryData? _currentTelemetry;

    public TelemetryData? CurrentTelemetry => _currentTelemetry;
    public event EventHandler<TelemetryData>? TelemetryUpdated;

    public TelemetryService(IConfigManager configManager, ILogger<TelemetryService> logger)
    {
        _configManager = configManager;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial sample
        SampleCpu();
        SampleNetwork();

        StartTimer();

        _configManager.ConfigChanged += (s, cfg) =>
        {
            StartTimer();
        };

        return Task.CompletedTask;
    }

    public void SetPaused(bool isPaused)
    {
        if (_isPaused == isPaused) return;
        _isPaused = isPaused;

        if (_isPaused)
        {
            _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _logger.LogDebug("Telemetry sampling suspended due to pause state.");
        }
        else
        {
            // Reset base times so delta isn't huge
            SampleCpu();
            SampleNetwork();
            StartTimer();
            _logger.LogDebug("Telemetry sampling resumed.");
        }
    }

    private void StartTimer()
    {
        _pollTimer?.Dispose();
        _pollTimer = new System.Threading.Timer(
            _ => PollMetrics(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private void PollMetrics()
    {
        if (_isPaused) return;

        try
        {
            var cpu = SampleCpu();
            var (ramPercent, ramUsedGb, ramTotalGb) = SampleRam();
            var gpu = SampleGpu();
            var (netDown, netUp) = SampleNetwork();

            _currentTelemetry = new TelemetryData
            {
                CpuPercent = Math.Round(cpu, 1),
                RamPercent = Math.Round(ramPercent, 1),
                RamUsedGb = Math.Round(ramUsedGb, 1),
                RamTotalGb = Math.Round(ramTotalGb, 1),
                GpuPercent = Math.Round(gpu, 1),
                NetDownloadKbps = Math.Round(netDown, 1),
                NetUploadKbps = Math.Round(netUp, 1),
                Timestamp = DateTime.UtcNow
            };

            TelemetryUpdated?.Invoke(this, _currentTelemetry);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Transient error sampling telemetry.");
        }
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0.0;
        }

        var idle = ToLong(idleTime);
        var kernel = ToLong(kernelTime);
        var user = ToLong(userTime);

        if (_prevCpuSampleTime == DateTime.MinValue)
        {
            _prevCpuIdle = idle;
            _prevCpuKernel = kernel;
            _prevCpuUser = user;
            _prevCpuSampleTime = DateTime.UtcNow;
            return 0.0;
        }

        var deltaIdle = idle - _prevCpuIdle;
        var deltaKernel = kernel - _prevCpuKernel;
        var deltaUser = user - _prevCpuUser;

        _prevCpuIdle = idle;
        _prevCpuKernel = kernel;
        _prevCpuUser = user;
        _prevCpuSampleTime = DateTime.UtcNow;

        var totalSystem = deltaKernel + deltaUser;
        if (totalSystem <= 0) return 0.0;

        var cpuUsage = (double)(totalSystem - deltaIdle) / totalSystem * 100.0;
        return Math.Clamp(cpuUsage, 0.0, 100.0);
    }

    private static (double RamPercent, double RamUsedGb, double RamTotalGb) SampleRam()
    {
        var memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));

        if (!GlobalMemoryStatusEx(ref memStatus))
        {
            return (0, 0, 0);
        }

        var totalGb = memStatus.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        var availGb = memStatus.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
        var usedGb = totalGb - availGb;
        var percent = memStatus.dwMemoryLoad;

        return (percent, usedGb, totalGb);
    }

    private double SampleGpu()
    {
        try
        {
            // Re-discover GPU 3D engine counters every 30 seconds
            if (!_gpuCountersInitialized || (DateTime.UtcNow - _lastGpuCounterCheck).TotalSeconds > 30)
            {
                RefreshGpuCounters();
            }

            if (_gpuCounters.Count == 0) return 0.0;

            float totalGpu = 0;
            for (var i = _gpuCounters.Count - 1; i >= 0; i--)
            {
                try
                {
                    totalGpu += _gpuCounters[i].NextValue();
                }
                catch
                {
                    // Instance may have terminated
                    _gpuCounters[i].Dispose();
                    _gpuCounters.RemoveAt(i);
                }
            }

            return Math.Clamp(totalGpu, 0.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private void RefreshGpuCounters()
    {
        _lastGpuCounterCheck = DateTime.UtcNow;
        foreach (var c in _gpuCounters)
        {
            try { c.Dispose(); } catch { }
        }
        _gpuCounters.Clear();

        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var instanceNames = category.GetInstanceNames();

            // Filter for 3D engine instances: pid_..._engtype_3D
            foreach (var instance in instanceNames)
            {
                if (instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                {
                    var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                    // Prime counter
                    _ = counter.NextValue();
                    _gpuCounters.Add(counter);
                }
            }
            _gpuCountersInitialized = true;
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "GPU Engine performance counters not accessible or not supported.");
            _gpuCountersInitialized = true;
        }
    }

    private (double NetDownKbps, double NetUpKbps) SampleNetwork()
    {
        try
        {
            long currentBytesRecv = 0;
            long currentBytesSent = 0;

            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                {
                    var stats = ni.GetIPStatistics();
                    currentBytesRecv += stats.BytesReceived;
                    currentBytesSent += stats.BytesSent;
                }
            }

            var now = DateTime.UtcNow;
            if (_prevNetSampleTime == DateTime.MinValue)
            {
                _prevBytesReceived = currentBytesRecv;
                _prevBytesSent = currentBytesSent;
                _prevNetSampleTime = now;
                return (0, 0);
            }

            var elapsedSeconds = (now - _prevNetSampleTime).TotalSeconds;
            if (elapsedSeconds <= 0.01) return (0, 0);

            var deltaRecv = Math.Max(0, currentBytesRecv - _prevBytesReceived);
            var deltaSent = Math.Max(0, currentBytesSent - _prevBytesSent);

            _prevBytesReceived = currentBytesRecv;
            _prevBytesSent = currentBytesSent;
            _prevNetSampleTime = now;

            var downKbps = (deltaRecv * 8.0) / (elapsedSeconds * 1000.0);
            var upKbps = (deltaSent * 8.0) / (elapsedSeconds * 1000.0);

            return (downKbps, upKbps);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static long ToLong(FILETIME ft) => ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] ref MEMORYSTATUSEX lpBuffer);

    public override void Dispose()
    {
        _pollTimer?.Dispose();
        foreach (var c in _gpuCounters)
        {
            try { c.Dispose(); } catch { }
        }
        _gpuCounters.Clear();
        base.Dispose();
    }
}
