namespace BleTerminal.Core;

/// <summary>Bytes-per-second between successive samples of a running byte total.</summary>
public sealed class RateMeter
{
    long _lastTotal;
    DateTime? _lastTime;

    public double Peak { get; private set; }

    public double Sample(long total, DateTime now)
    {
        double rate = 0;
        if (_lastTime is DateTime t)
        {
            double dt = (now - t).TotalSeconds;
            if (dt > 0) rate = Math.Max(0, (total - _lastTotal) / dt);
        }
        _lastTotal = total;
        _lastTime = now;
        if (rate > Peak) Peak = rate;
        return rate;
    }

    public void Reset()
    {
        _lastTotal = 0;
        _lastTime = null;
        Peak = 0;
    }
}

public static class Signal
{
    /// <summary>Four-step bar indicator for an RSSI in dBm (monospaced font expected).</summary>
    public static string Bars(int dbm) =>
        dbm >= -60 ? "\u2582\u2584\u2586\u2588"
        : dbm >= -70 ? "\u2582\u2584\u2586 "
        : dbm >= -80 ? "\u2582\u2584  "
        : "\u2582   ";
}
