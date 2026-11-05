using System.Globalization;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>One line per event to the container log (stdout, which the image's entry script hands on), each with the local time - and
/// the same line to a test's list. LogOnce keeps a 1 s loop from flooding it.</summary>
public sealed class Log(string prefix, IClock clock, TimeZoneInfo zone, Action<string>? sink = null)
{
    public const double AlertRepeat = 1800;
    readonly Dictionary<string, (string Message, double At)> lastSaid = [];
    public List<string> Lines { get; } = [];
    public bool Echo { get; set; } = sink is null;          // to stdout (the program); a test keeps its lines

    public void Line(string message)
    {
        Lines.Add(message);
        if (Lines.Count > 20000)
            Lines.RemoveRange(0, 10000);
        sink?.Invoke(message);
        if (Echo)
        {
            var at = TimeZoneInfo.ConvertTime(clock.Now, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            Console.Out.WriteLine($"{prefix}: {at} {message}");
            Console.Out.Flush();
        }
    }

    /// <summary>Log when the wording changes, else again after `repeat` seconds.</summary>
    public void Once(string key, string message, double repeat = AlertRepeat)
    {
        var now = Watch.Seconds(clock.Now);
        if (lastSaid.TryGetValue(key, out var previous) && previous.Message == message && now - previous.At < repeat)
            return;
        lastSaid[key] = (message, now);
        Line(message);
    }

    public void Forget() => lastSaid.Clear();
}
