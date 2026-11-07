using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>&lt;state&gt;/state.json: the items this tool has seen and what it decided, the update baseline, the day's downloads, the ratings,
/// the history, the last run. Read and written as a JSON object, not as classes: a key this version does not read stays as it was.</summary>
public sealed class SyncState(string path)
{
    public string Path { get; } = path;
    public JsonObject Data { get; set; } = new() { ["schema"] = 1, ["items"] = new JsonObject(), ["baseline"] = new JsonObject() };

    public JsonObject Items => Section("items");
    public JsonObject Baseline => Section("baseline");

    /// <summary>A section of the state, made when it is missing.</summary>
    public JsonObject Section(string name)
    {
        if (Data[name] is JsonObject o)
            return o;
        var fresh = new JsonObject();
        Data[name] = fresh;
        return fresh;
    }

    public SyncState Load()
    {
        var data = Node.ReadJson(Path);
        if (data is null && File.Exists(Path))
            throw new BadDataException($"{Path} does not parse - fix or delete it");
        if (data is JsonObject o)
        {
            Data = o;
            _ = Items;
            _ = Baseline;
        }
        return this;
    }

    public void Save() => Files.WriteAtomic(Path, Node.Compact(Data) + "\n", FileModes.Private);

    public JsonObject? Item(string wid) => Items[wid] as JsonObject;

    /// <summary>The Workshop's numbers of each map as the check last saw it: what workshop.json gives the bridge.</summary>
    public JsonObject Workshop => Section("workshop");

    /// <summary>An item's Workshop numbers as the check saw them (result 1; a map gone or hidden keeps what was seen before).</summary>
    public void NoteWorkshop(string wid, JsonObject item)
    {
        if (Node.Int(item["result"]) != 1)
            return;
        Workshop[wid] = new JsonObject
        {
            ["created"] = Node.Int(item["time_created"]), ["updated"] = Node.Int(item["time_updated"]), ["content"] = Node.StrOr(item["hcontent_file"]),
            ["subs"] = Node.Int(item["subscriptions"]), ["lifetime"] = Node.Int(item["lifetime_subscriptions"]), ["favs"] = Node.Int(item["favorited"]),
            ["lifetimeFavs"] = Node.Int(item["lifetime_favorited"]), ["views"] = Node.Int(item["views"]),
        };
    }

    /// <summary>Record a Workshop item we were shown (discovery, or a changed listed map), keeping what we decided before. "attempts" is the
    /// download tries of one content version: a newer version has them all again, and a changed listed map that used them up on an older
    /// one is queued for the new one.</summary>
    public JsonObject Remember(JsonObject item, string day, string why)
    {
        var wid = Node.Str(item["publishedfileid"]);
        if (Items[wid] is not JsonObject rec)
        {
            rec = new JsonObject { ["first"] = day, ["status"] = "queued", ["why"] = why, ["attempts"] = 0, ["tests"] = 0 };
            Items[wid] = rec;
        }
        var newer = Node.Int(item["time_updated"]) > Node.Int(rec["updated"]);
        var verdictOf = (JsonObject)item.DeepClone();
        if (!verdictOf.ContainsKey("result"))
            verdictOf["result"] = 1;
        rec["title"] = Text.ItemName(item);
        rec["tags"] = Node.Strings(Text.ItemTags(item));
        rec["size"] = Node.Int(item["file_size"]);
        rec["created"] = Node.Int(item["time_created"]);
        rec["updated"] = Node.Int(item["time_updated"]);
        rec["subs"] = Node.Int(item["subscriptions"]);
        rec["lifetime"] = Node.Int(item["lifetime_subscriptions"]);
        rec["filename"] = Node.Truthy(item["filename"]) ? item["filename"]!.DeepClone() : "";
        rec["app"] = Node.Int(item["consumer_appid"]);
        rec["creator"] = Node.StrOr(item["creator"]);
        rec["ws"] = Text.WorkshopVerdict(verdictOf);
        rec["preview"] = Node.Truthy(item["preview_url"]) ? item["preview_url"]!.DeepClone() : "";
        rec["description"] = Text.Cut(Text.CleanDescription(Node.StrOr(item["file_description"])), 300);
        if (newer)
            rec["attempts"] = 0;
        if (why == "changed" && (Node.Str(rec["status"]) is "published" or "pass" || (newer && Node.Str(rec["status"]) == "failed")))
        {
            rec["status"] = "queued";
            rec["why"] = "changed";
        }
        return rec;
    }

    /// <summary>Whether this version of a map failed its test today. A version that fails stays ahead of the baseline, so every check finds the
    /// map changed for as long as the Workshop keeps it: this is what holds it to one test a day.</summary>
    public bool FailedToday(string wid, JsonObject item, string day) =>
        Item(wid) is { } rec && Node.Str(rec["decided"]) == day && Node.Truthy(rec["verdict"]) && Node.Str(rec["verdict"]) != "pass"
        && Node.Int(item["time_updated"]) <= Node.Int(rec["updated"]);
}
