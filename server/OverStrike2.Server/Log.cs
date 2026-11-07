using System.Globalization;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>One line per event to the container log (stdout, which the image's entry script hands on), each with the local time - or, in
/// a test, to its sink. Once keeps a 1 s loop from flooding it.</summary>
public sealed class Log(string prefix, IClock clock, TimeZoneInfo zone, Action<string>? sink = null)
{
    public const double AlertRepeat = 1800;
    readonly Dictionary<string, (string Message, double At)> lastSaid = [];

    public void Line(string message)
    {
        sink?.Invoke(message);
        if (sink is null)
        {
            var at = TimeZoneInfo.ConvertTime(clock.Now, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Console.Out.WriteLine($"{prefix}: {at} {message}");
            Console.Out.Flush();
        }
    }

    /// <summary>Log when the wording changes, else again after `repeat` seconds.</summary>
    public void Once(string key, string message, double repeat = AlertRepeat)
    {
        var now = UnixTime.Seconds(clock.Now);
        if (lastSaid.TryGetValue(key, out var previous) && previous.Message == message && now - previous.At < repeat)
            return;
        lastSaid[key] = (message, now);
        Line(message);
    }
}
