namespace OverStrike2.Core;

/// <summary>THE STEAM WEB API'S ONE DOOR, per program: its calls go one at a time, each at least the gap after the last one ended
/// (2 s at least, the rule for every Web API caller). Programs do not share it: each keeps its own.</summary>
public sealed class WebApiDoor(double gapSeconds, IClock? clock = null)
{
    public const double MinimumGap = 2.0;
    readonly IClock clock = clock ?? SystemClock.Instance;
    readonly SemaphoreSlim one = new(1, 1);
    readonly TimeSpan gap = TimeSpan.FromSeconds(Math.Max(MinimumGap, gapSeconds));
    DateTimeOffset lastEnded = DateTimeOffset.MinValue;

    public TimeSpan Gap => gap;

    /// <summary>Run a call through the door: wait for any call in flight, sleep out what is left of the gap, call, note when it ended.</summary>
    public T Call<T>(Func<T> call)
    {
        one.Wait();
        try
        {
            if (lastEnded != DateTimeOffset.MinValue)
                clock.Sleep(lastEnded + gap - clock.Now);
            try
            {
                return call();
            }
            finally
            {
                lastEnded = clock.Now;
            }
        }
        finally
        {
            one.Release();
        }
    }
}
