using System.Globalization;

namespace AutoCompressor.Core.Util;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "-" + Bytes(-bytes);
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        string fmt = unit == 0 ? "0" : value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return value.ToString(fmt, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    public static string Duration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Bitrate(long bitsPerSecond)
    {
        if (bitsPerSecond <= 0) return "";
        return bitsPerSecond >= 1_000_000
            ? (bitsPerSecond / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"
            : (bitsPerSecond / 1000.0).ToString("0", CultureInfo.InvariantCulture) + " kbps";
    }

    public static string Percent(double fraction) => (fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Invariant(double value, string format = "0.###") => value.ToString(format, CultureInfo.InvariantCulture);
}
