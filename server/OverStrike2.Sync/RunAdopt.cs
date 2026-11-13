// A run's hand verdicts: <state>/adopt.json, non-maps found some other way than this sync's own test (an older sweep, a scan by hand), taken into its
// state at a run's start and published by its own publish step like its own verdicts - the same plan, the same merge check, onto broken.json's packs.
using System.Globalization;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>THE HAND VERDICTS: what &lt;state&gt;/adopt.json may say. broken.json has one writer, this sync, and its publish only ever adds; a verdict
/// it did not reach itself comes in through its state, as a record of its own (why "adopted"), so the publish that writes its own non-maps writes these
/// too - no second writer, nothing around the merge check.
///
///     {"nonmaps": [{"id": 3353017497, "name": "...", "kind": "pack", "evidence": "confirmed by test", "list": "tail", "phrase": null,
///                   "detail": "no maps/&lt;name&gt;.vpk in its VPK, only 341 compiled models - an asset pack (sweep 2026-10-05)"}]}
///
/// kind, evidence, list and phrase are broken.json's packs fields, as its "fields" describes them; detail says what the verdict rests on, with its date;
/// preview, optional, is an https URL. An entry that breaks a rule is skipped and said, the others are taken.</summary>
public static class Adopt
{
    public const string FileName = "adopt.json";       // dropped into the state folder by hand, like run-now
    public const string Folder = "adopted";            // a file taken in is kept there, by its time: <yyyyMMdd-HHmmss>.json, .refused.json when it could not be read
    public const string Why = "adopted";               // a hand verdict's record says so where the sync's own say new or changed
    public const int DetailMax = 300, PhraseMax = 200;

    public static readonly string[] Kinds = ["pack", "empty", "stub", "legacy"];
    public static readonly string[] Evidence = ["confirmed by test", "identified from description", "identified from its Workshop file"];
    public static readonly string[] Lists = ["live", "spare", "newer", "tail", "sync", "scrape"];

    public sealed record Entry(string Id, string Name, string Kind, string Evidence, string Detail, string List, string? Phrase, string Preview);

    static string? Pick(JsonNode? v, string[] allowed) => Node.IsString(v) && allowed.Contains(Node.Str(v)) ? Node.Str(v) : null;

    /// <summary>The file's entries: the ones that can be taken, and (id - #n for one without -, why) of the ones that cannot. Throws BadDataException
    /// when the file is not {"nonmaps": [...]}: then nothing in it is taken.</summary>
    public static (List<Entry> Ok, List<(string Id, string Why)> Refused) Parse(JsonNode? doc)
    {
        if (doc is not JsonObject o || o["nonmaps"] is not JsonArray list)
            throw new BadDataException($"{FileName} is not {{\"nonmaps\": [...]}}");
        var ok = new List<Entry>();
        var refused = new List<(string, string)>();
        var seen = new HashSet<string>();
        for (var i = 0; i < list.Count; i++)
        {
            var e = list[i] as JsonObject;
            var id = Node.Id(e?["id"]) is { } raw && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n.ToString(CultureInfo.InvariantCulture) : null;     // the id as every list writes it: no leading zeros
            var why = e is null ? "not an object"
                : id is null ? "no id (a Workshop id, digits)"
                : !seen.Add(id) ? "named twice in the file"
                : Pick(e["kind"], Kinds) is null ? $"its kind is none of {string.Join(", ", Kinds)}"
                : Pick(e["evidence"], Evidence) is null ? $"its evidence is none of {string.Join(", ", Evidence.Select(x => $"'{x}'"))}"
                : Pick(e["list"], Lists) is null ? $"its list is none of {string.Join(", ", Lists)}"
                : Text.Clean(Node.StrOr(e["detail"])).Length == 0 ? "no detail: what the verdict rests on, with its date"
                : e["phrase"] is { } p && !Node.IsString(p) ? "its phrase is not text"
                : null;
            if (why is not null)
            {
                refused.Add((id ?? $"#{i + 1}", why));
                continue;
            }
            var name = Text.Clean(Node.StrOr(e!["name"]));
            var phrase = Text.Clean(Node.StrOr(e["phrase"]), PhraseMax);
            var preview = Node.StrOr(e["preview"]);
            ok.Add(new Entry(id!, name.Length > 0 ? name : $"Workshop item {id}", Pick(e["kind"], Kinds)!, Pick(e["evidence"], Evidence)!,
                             Text.Clean(Node.Str(e["detail"]), DetailMax), Pick(e["list"], Lists)!, phrase.Length > 0 ? phrase : null,
                             preview.StartsWith("https://", StringComparison.Ordinal) ? preview : ""));
        }
        return (ok, refused);
    }
}

public sealed partial class SyncRun
{
    /// <summary>The hand verdicts, at a run's start: &lt;state&gt;/adopt.json read, and each entry the sync may take recorded in its state as a non-map of
    /// its own (status tested, why adopted) - this run's publish writes it onto broken.json's packs as it writes the sync's own -, the file then kept in
    /// adopted/, so it is taken once. Not taken: an id the bridge's lists know (listed in any mode, on broken.json or blocked.json, excluded by the
    /// settings), one the state has a verdict on, and one this sync is about to test itself. A file it cannot read takes nothing, is kept as
    /// .refused.json, and is a problem for the mail.</summary>
    public void TakeHandVerdicts(IReadOnlyDictionary<string, string> known)
    {
        var path = Path.Combine(Cfg.StateDir, Adopt.FileName);
        if (!File.Exists(path))
            return;
        var stamp = Clock.Local().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var report = new JsonObject();
        Report["adopt"] = report;
        List<Adopt.Entry> ok;
        List<(string Id, string Why)> refused;
        try
        {
            (ok, refused) = Adopt.Parse(Node.Parse(File.ReadAllBytes(path), Adopt.FileName));
        }
        catch (Exception e) when (e is BadDataException or IOException or UnauthorizedAccessException)
        {
            var keptBad = KeepHandFile(path, stamp, ".refused.json");
            report["refused"] = e.Message;
            report["file"] = keptBad;
            Errors.Add($"{Adopt.FileName} not taken in: {e.Message} - nothing in it was adopted{(keptBad is not null ? $"; it is kept as {keptBad}" : "")}");
            L($"adopt: {Adopt.FileName} not taken in ({e.Message})");
            return;
        }
        var taken = new JsonArray();
        var skipped = new JsonArray();
        foreach (var (id, why) in refused)
            skipped.Add(Node.Array(id, why));
        foreach (var e in ok)
        {
            var rec = State.Item(e.Id);
            var why = known.TryGetValue(e.Id, out var where) ? $"known already ({where})"
                : rec is not null && Node.Truthy(rec["verdict"])
                    ? $"the state has a verdict on it already ({Node.Str(rec["verdict"])}{(Node.Str(rec["why"]) == Adopt.Why ? ", adopted" : "")})"
                : rec is not null && Node.Str(rec["status"]) is "queued" or "retry" or "downloaded" or "tested" ? $"this sync tests it itself ({Node.Str(rec["status"])})"
                : null;
            if (why is not null)
            {
                skipped.Add(Node.Array(e.Id, why));
                continue;
            }
            // a record the state has (a CS:GO-era upload the queue skipped, a download that failed) keeps what the Workshop said of the item, its title too
            rec ??= new JsonObject { ["first"] = Day0, ["attempts"] = 0, ["tests"] = 0, ["tags"] = new JsonArray(), ["size"] = 0, ["description"] = "" };
            rec["status"] = "tested";
            rec["why"] = Adopt.Why;
            rec["adopted"] = Day0;
            if (!Node.Truthy(rec["title"]))
                rec["title"] = e.Name;
            rec["verdict"] = "nonmap";
            rec["detail"] = new JsonObject { ["kind"] = e.Kind, ["detail"] = e.Detail, ["evidence"] = e.Evidence, ["list"] = e.List, ["phrase"] = e.Phrase };
            if (!Node.Truthy(rec["preview"]))
                rec["preview"] = e.Preview;
            State.Items[e.Id] = rec;
            taken.Add(Node.Array(e.Id, Node.Str(rec["title"]), e.Kind));
        }
        State.Save();
        var kept = KeepHandFile(path, stamp, ".json");
        report["file"] = kept;
        report["adopted"] = taken;
        report["skipped"] = skipped;
        L($"adopt: {taken.Count} hand verdict(s) taken in from {Adopt.FileName}, {skipped.Count} skipped{(kept is not null ? $" - the file is kept as {kept}" : "")}");
        foreach (var row in skipped.Select(Node.Arr))
            L($"adopt: {Node.Str(row[0])} skipped - {Node.Str(row[1])}");
    }

    /// <summary>The hand file moved to adopted/&lt;stamp&gt;&lt;suffix&gt; (-2, -3 ... when that is taken), so it is taken once: its path from the state folder, or
    /// null when it could not be moved - said in the log; the next run reads it again and skips what this one took, which the state holds now.</summary>
    string? KeepHandFile(string path, string stamp, string suffix)
    {
        try
        {
            var folder = Path.Combine(Cfg.StateDir, Adopt.Folder);
            Directory.CreateDirectory(folder);
            var name = stamp + suffix;
            for (var n = 2; File.Exists(Path.Combine(folder, name)); n++)
                name = $"{stamp}-{n}{suffix}";
            File.Move(path, Path.Combine(folder, name));
            return $"{Adopt.Folder}/{name}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            L($"adopt: {Adopt.FileName} not moved to {Adopt.Folder}/ ({e.Message})");
            return null;
        }
    }
}
