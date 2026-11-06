using System;

namespace LiveWallpaper.Core.Server;

public static class ServerMetricCalculator
{
    public static MetricPoint Calculate(RawServerMetrics current, RawServerMetrics? previous)
    {
        ArgumentNullException.ThrowIfNull(current);

        double cpu = 0.0;
        double rxKbps = 0.0;
        double txKbps = 0.0;

        if (previous != null)
        {
            cpu = CalculateCpuPercent(current.Cpu, previous.Cpu);

            double dt = current.T - previous.T;
            if (dt <= 0)
            {
                dt = 2.0; // fallback standard 2-second interval
            }

            long deltaRx = current.Net.Rx - previous.Net.Rx;
            long deltaTx = current.Net.Tx - previous.Net.Tx;

            if (deltaRx >= 0)
            {
                rxKbps = deltaRx / (1024.0 * dt);
            }

            if (deltaTx >= 0)
            {
                txKbps = deltaTx / (1024.0 * dt);
            }
        }

        double mem = 0.0;
        if (current.Mem.Total > 0)
        {
            double used = current.Mem.Total - current.Mem.Avail;
            mem = Math.Clamp(used * 100.0 / current.Mem.Total, 0.0, 100.0);
        }

        double swap = 0.0;
        if (current.Swap.Total > 0)
        {
            double used = current.Swap.Total - current.Swap.Free;
            swap = Math.Clamp(used * 100.0 / current.Swap.Total, 0.0, 100.0);
        }

        double diskPct = 0.0;
        if (current.Disk.Total > 0)
        {
            diskPct = Math.Clamp(current.Disk.Used * 100.0 / current.Disk.Total, 0.0, 100.0);
        }

        double load1 = current.Load.Length > 0 ? current.Load[0] : 0.0;

        return new MetricPoint
        {
            T = current.T,
            Cpu = Math.Round(cpu, 1),
            Mem = Math.Round(mem, 1),
            Swap = Math.Round(swap, 1),
            Load1 = Math.Round(load1, 2),
            DiskPct = Math.Round(diskPct, 1),
            RxKbps = Math.Round(rxKbps, 1),
            TxKbps = Math.Round(txKbps, 1)
        };
    }

    private static double CalculateCpuPercent(long[] cur, long[] prev)
    {
        if (cur.Length < 4 || prev.Length < 4) return 0.0;

        long curTotal = 0;
        for (int i = 0; i < cur.Length; i++) curTotal += cur[i];

        long prevTotal = 0;
        for (int i = 0; i < prev.Length; i++) prevTotal += prev[i];

        long curIdle = cur[3];
        if (cur.Length > 4) curIdle += cur[4]; // iowait

        long prevIdle = prev[3];
        if (prev.Length > 4) prevIdle += prev[4];

        long deltaTotal = curTotal - prevTotal;
        long deltaIdle = curIdle - prevIdle;

        if (deltaTotal <= 0 || deltaIdle < 0 || deltaTotal < deltaIdle)
        {
            return 0.0; // Counter wrapped, reset, or reboot
        }

        double pct = (deltaTotal - deltaIdle) * 100.0 / deltaTotal;
        return Math.Clamp(pct, 0.0, 100.0);
    }
}
