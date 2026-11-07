using System.Globalization;

namespace OverStrike2.Core;

/// <summary>Words for logs and messages.</summary>
public static class Words
{
    /// <summary>"0h30m": hours and minutes.</summary>
    public static string Hm(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return $"{s / 3600}h{s % 3600 / 60:00}m";
    }

    /// <summary>"1h05m", "5m03s", "42s": the two largest units.</summary>
    public static string Duration(double seconds)
    {
        var s = (long)Math.Max(0, seconds);
        return s >= 3600 ? $"{s / 3600}h{s % 3600 / 60:00}m" : s >= 60 ? $"{s / 60}m{s % 60:00}s" : $"{s}s";
    }

    /// <summary>"1.4 GB" from a gigabyte on, else "730 MB".</summary>
    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    /// <summary>What may be handed to RCON say: one line, no double quotes or semicolons, at most `limit` characters.</summary>
    public static string ChatSafe(string text, int limit = 120)
    {
        var t = new string(text.Select(c => c == '"' ? '\'' : c == ';' ? ',' : c < ' ' ? ' ' : c).ToArray()).Trim();
        return t.Length <= limit ? t : t[..(limit - 3)] + "...";
    }
}
