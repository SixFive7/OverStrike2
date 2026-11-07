using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>Every Web API call this tool makes, persisted BEFORE it is sent (&lt;state&gt;/api-ledger.json): the rolling 24 h cap, the count
/// per day, the day's stop - so a crash never loses one, and a restart cannot resume a stop. An unreadable ledger means no calls at all.
/// The per-run cap is SteamApi's.</summary>
public sealed class ApiLedger(string path, int dayCap, SyncClock clock)
{
    public const double Day = 86400;
    public string Path { get; } = path;
    public int DayCap { get; } = Math.Min(dayCap, SyncConfig.HardApiDayCap);
    public JsonObject? Data { get; set; }
    public string? Broken { get; private set; }

    public void Load()
    {
        if (!File.Exists(Path))
        {
            Data = new JsonObject { ["schema"] = 1, ["calls"] = new JsonArray(), ["days"] = new JsonObject(), ["stops"] = new JsonObject() };
            return;
        }
        var data = Node.ReadJson(Path);
        if (data is not JsonObject o || o["calls"] is not JsonArray calls || o["days"] is not JsonObject
            || calls.Any(c => c is not JsonArray a || a.Count == 0 || KeepSet.Number(a[0]) is null))
        {
            Broken = $"{Path} does not parse - no Web API call until it is fixed or deleted";
            Data = null;
            return;
        }
        if (o["stops"] is not JsonObject)
            o["stops"] = new JsonObject();
        Data = o;
    }

    public void Save() => Files.WriteAtomic(Path, Node.Compact(Data) + "\n", FileModes.Private);

    static double Time(JsonNode? call) => call is JsonArray a && a.Count > 0 && KeepSet.Number(a[0]) is { } t ? t : double.NegativeInfinity;

    void Prune(double now)
    {
        var calls = (JsonArray)Data!["calls"]!;
        var keep = calls.Where(c => c is JsonArray a && a.Count > 0 && now - Time(c) < Day).Select(c => c!.DeepClone()).ToList();
        calls.Clear();
        foreach (var c in keep)
            calls.Add(c);
        foreach (var section in new[] { "days", "stops" })
        {
            var table = (JsonObject)Data[section]!;
            foreach (var old in table.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).SkipLast(14).ToList())
                table.Remove(old);
        }
    }

    public int Last24h(double? now = null)
    {
        var t = now ?? clock.Now();
        return Node.Arr(Data?["calls"]).Count(c => t - Time(c) < Day);
    }

    public long Count(string day) => Node.Int(Node.Get(Data?["days"], day));

    public string? Stopped(string day) => Node.Truthy(Node.Get(Data?["stops"], day)) ? Node.Str(Node.Get(Data?["stops"], day)) : null;

    public void Stop(string day, string why)
    {
        if (Data is null)
            return;
        var stops = (JsonObject)Data["stops"]!;
        if (!Node.Truthy(stops[day]))
        {
            stops[day] = why;
            Save();
        }
    }

    /// <summary>Count a call before it is made. Throws StopApiException when a cap or the day's stop says no.</summary>
    public int Reserve(string day, string api)
    {
        if (Data is null)
            throw new StopApiException(Broken ?? "the API ledger is not loaded");
        var now = clock.Now();
        Prune(now);
        if (Stopped(day) is { } stopped)
            throw new StopApiException($"stopped earlier today: {stopped}");
        if (Last24h(now) >= DayCap)
            throw new StopApiException($"the cap of {DayCap} Web API calls in 24 hours is reached");
        var calls = (JsonArray)Data["calls"]!;
        calls.Add(new JsonArray(Math.Round(now, 3), api, null));
        ((JsonObject)Data["days"]!)[day] = Count(day) + 1;
        Save();
        return calls.Count - 1;
    }

    public void Settle(int index, JsonNode? status)
    {
        try
        {
            ((JsonArray)((JsonArray)Data!["calls"]!)[index]!)[2] = status;
            Save();
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidCastException or NullReferenceException or IOException or UnauthorizedAccessException)
        {
        }
    }
}
