using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>One restart in the history: when, and which kind.</summary>
public sealed record Restart(long At, string Kind);

/// <summary>The restart that a run of the watcher triggered, judged on the next start: its kind, when, and what it should change - the
/// CS2 build it left (From) and the one Valve required (Required) for an update; the version of our own parts the engine ran (Installed)
/// and the one on disk (Source) for every planned kind.</summary>
public sealed record Pending(string Kind, long At, string? From, int? Required, string? Installed = null, string? Source = null);

/// <summary>LOOP PROTECTION, persisted in update-watch/state.json on the game's volume - the container restarting is the very thing being
/// counted, so memory would be worthless. Schema 3 (schema 2, the old watcher's, is read and written back as 3): schema 2 plus the
/// restart kind "version", the pending record's Installed and Source, and VersionTrippedFor. A file that exists but does not type-check is
/// a HARD FAULT, never a reason to start counting from zero: that would hand a corrupting bug a fresh budget on every boot.</summary>
public sealed class WatchState
{
    public const int Schema = 3;
    public List<Restart> Restarts { get; set; } = [];
    public string DoneDay { get; set; } = "";
    public List<int> Durations { get; set; } = [];
    public Pending? Pending { get; set; }
    public long UpdateMisses { get; set; }
    public long UpdateRetryAfter { get; set; }
    public long UpdateTripped { get; set; }
    public long UpdateTrippedFor { get; set; }
    public long FreezeMisses { get; set; }
    public long FreezeTripped { get; set; }
    public string VersionTrippedFor { get; set; } = "";     // the version on disk a restart did not install: none for it again

    public WatchState Copy() => FromJson(ToJson()).State;

    public string ToJson()
    {
        var o = new JsonObject
        {
            ["done_day"] = DoneDay,
            ["durations"] = new JsonArray(Durations.Select(d => (JsonNode)d).ToArray()),
            ["freeze_misses"] = FreezeMisses,
            ["freeze_tripped"] = FreezeTripped,
            ["pending"] = Pending is null ? null : new JsonObject
            {
                ["at"] = Pending.At,
                ["from"] = Pending.From,
                ["installed"] = Pending.Installed,
                ["kind"] = Pending.Kind,
                ["required"] = Pending.Required,
                ["source"] = Pending.Source,
            },
            ["restarts"] = new JsonArray(Restarts.Select(r => (JsonNode)new JsonArray(r.At, r.Kind)).ToArray()),
            ["schema"] = Schema,
            ["update_misses"] = UpdateMisses,
            ["update_retry_after"] = UpdateRetryAfter,
            ["update_tripped"] = UpdateTripped,
            ["update_tripped_for"] = UpdateTrippedFor,
            ["version_tripped_for"] = VersionTrippedFor,
        };
        return o.ToJsonString() + "\n";
    }

    static bool Int(JsonNode? n, out long v)
    {
        v = 0;
        return n is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && KeepSet.Integer(n) is { } i && (v = i) == i;
    }

    /// <summary>(state, null) or (defaults, why).</summary>
    public static (WatchState State, string? Fault) FromJson(string text)
    {
        JsonNode? loaded;
        try
        {
            loaded = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            return (new WatchState(), $"does not parse ({e.Message})");
        }
        return Validate(loaded);
    }

    public static (WatchState State, string? Fault) Validate(JsonNode? loaded)
    {
        var s = new WatchState();
        if (loaded is not JsonObject o)
            return (s, "not an object");
        var schema = o["schema"];
        if (schema is not null && !(Int(schema, out var sv) && sv is 2 or 3))
            return (s, $"unknown schema {schema.ToJsonString()} (delete the file to start over)");
        foreach (var (key, set) in new (string, Action<long>)[]
                 {
                     ("update_misses", v => s.UpdateMisses = v), ("update_retry_after", v => s.UpdateRetryAfter = v),
                     ("update_tripped", v => s.UpdateTripped = v), ("update_tripped_for", v => s.UpdateTrippedFor = v),
                     ("freeze_misses", v => s.FreezeMisses = v), ("freeze_tripped", v => s.FreezeTripped = v),
                 })
        {
            var n = o[key];
            if (n is null)
                continue;
            if (!Int(n, out var v) || v < 0)
                return (new WatchState(), $"bad {key} ({n.ToJsonString()})");
            set(v);
        }
        if (o["done_day"] is { } day)
        {
            if (day is not JsonValue dv || dv.GetValueKind() != JsonValueKind.String || dv.GetValue<string>().Length > 10)
                return (new WatchState(), $"bad done_day ({day.ToJsonString()})");
            s.DoneDay = dv.GetValue<string>();
        }
        if (o["version_tripped_for"] is { } vt)
        {
            if (vt is not JsonValue vv || vv.GetValueKind() != JsonValueKind.String || vv.GetValue<string>().Length > 64)
                return (new WatchState(), $"bad version_tripped_for ({vt.ToJsonString()})");
            s.VersionTrippedFor = vv.GetValue<string>();
        }
        if (o["restarts"] is { } restarts)
        {
            if (restarts is not JsonArray ra || ra.Count > 1000)
                return (new WatchState(), "bad restarts list");
            foreach (var e in ra)
            {
                if (e is not JsonArray pair || pair.Count != 2 || !Int(pair[0], out var at) || at < 0 || pair[1] is not JsonValue kv
                    || kv.GetValueKind() != JsonValueKind.String || !Kinds.All.Contains(kv.GetValue<string>()))
                    return (new WatchState(), $"bad restarts entry ({e?.ToJsonString() ?? "null"})");
                s.Restarts.Add(new Restart(at, kv.GetValue<string>()));
            }
        }
        if (o["durations"] is { } durations)
        {
            if (durations is not JsonArray da)
                return (new WatchState(), $"bad durations ({durations.ToJsonString()})");
            foreach (var d in da)
            {
                if (!Int(d, out var v) || v <= 0 || v >= 3600)
                    return (new WatchState(), $"bad durations ({durations.ToJsonString()})");
                s.Durations.Add((int)v);
            }
            if (s.Durations.Count > 5)
                s.Durations = s.Durations[^5..];
        }
        if (o["pending"] is { } pending)
        {
            if (pending is not JsonObject p || p["kind"] is not JsonValue pk || pk.GetValueKind() != JsonValueKind.String
                || !Kinds.All.Contains(pk.GetValue<string>()) || !Int(p["at"], out var pat) || pat < 0
                || !OptionalText(p["from"], 32, out var from) || !OptionalText(p["installed"], 64, out var installed)
                || !OptionalText(p["source"], 64, out var source))
                return (new WatchState(), $"bad pending record ({pending.ToJsonString()})");
            int? required = null;
            if (p["required"] is { } rq)
            {
                if (!Int(rq, out var r) || r <= 0 || r > int.MaxValue)
                    return (new WatchState(), $"bad pending record ({pending.ToJsonString()})");
                required = (int)r;
            }
            s.Pending = new Pending(pk.GetValue<string>(), pat, from, required, installed, source);
        }
        return (s, null);
    }

    static bool OptionalText(JsonNode? n, int max, out string? value)
    {
        value = null;
        if (n is null)
            return true;
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.String || v.GetValue<string>().Length > max)
            return false;
        value = v.GetValue<string>();
        return true;
    }

    /// <summary>The state file, or the defaults and why it could not be used. No file: the defaults, no fault.</summary>
    public static (WatchState State, string? Fault) Load(string path)
    {
        if (!File.Exists(path))
            return (new WatchState(), null);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new WatchState(), $"{path} is unreadable ({e.Message})");
        }
        var (state, fault) = FromJson(text);
        return (state, fault is null ? null : $"{path}: {fault}");
    }

    /// <summary>Atomic and durable: the counters must be on disk before a quit or a kill takes the container down.</summary>
    public void Save(string path) => Files.WriteAtomic(path, Encoding.UTF8.GetBytes(ToJson()));

    /// <summary>Null when the state folder can be created, written, read back and cleaned; else why not.</summary>
    public static string? PersistenceSelftest(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".writable");
            using (var f = new FileStream(probe, FileMode.Create, FileAccess.Write))
            {
                f.Write("ok"u8);
                f.Flush(true);
            }
            var back = File.ReadAllText(probe);
            File.Delete(probe);
            return back == "ok" ? null : $"wrote 'ok', read back '{back}'";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }
}
