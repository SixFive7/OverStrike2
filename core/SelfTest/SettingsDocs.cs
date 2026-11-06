using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OverStrike2.Core.SelfTest;

/// <summary>docs/settings.schema.json and docs/settings.md's key tables, made from <see cref="Settings.Keys"/>: what the self-test holds the
/// committed files to, and what --write writes. Only the self-test makes them, so none of the programs that read the settings carries them.</summary>
static class SettingsDocs
{
    /// <summary>docs/settings.schema.json, made from <see cref="Settings.Keys"/>: what the self-test compares the committed file with.</summary>
    public static string JsonSchema()
    {
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Over Strike 2 settings",
            ["description"] = "The settings file every part of Over Strike 2 reads (docs/settings.md). JSON with comments; a string \"env:NAME\" or \"file:PATH\" is read from there.",
            ["type"] = "object",
            ["required"] = new JsonArray("schema"),
        };
        var props = new JsonObject
        {
            ["$schema"] = new JsonObject { ["type"] = "string" },
            ["schema"] = new JsonObject { ["const"] = Settings.Schema, ["description"] = "The file's format; this code reads schema " + Settings.Schema + "." },
        };
        root["properties"] = props;
        foreach (var key in Settings.Keys)
        {
            var parts = key.Path.Split('.');
            var node = props;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (node[parts[i]] is not JsonObject section)
                {
                    section = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                    node[parts[i]] = section;
                }
                node = (JsonObject)section["properties"]!;
            }
            var p = new JsonObject { ["description"] = key.Doc + (key.Replaces is "(new)" ? "" : " Replaces " + key.Replaces + ".") };
            switch (key.Type)
            {
                case "boolean":
                    p["type"] = "boolean";
                    p["default"] = key.Default == "true";
                    break;
                case "integer":
                    p["type"] = "integer";
                    p["default"] = int.Parse(key.Default, CultureInfo.InvariantCulture);
                    p["minimum"] = key.Min;
                    p["maximum"] = key.Max;
                    break;
                case "number":
                    p["type"] = "number";
                    p["default"] = double.Parse(key.Default, CultureInfo.InvariantCulture);
                    p["minimum"] = key.Min;
                    p["maximum"] = key.Max;
                    break;
                case "string list":
                    p["type"] = "array";
                    p["items"] = new JsonObject { ["type"] = "string" };
                    p["default"] = JsonNode.Parse(key.Default);
                    break;
                case "time":
                    p["type"] = "string";
                    p["pattern"] = "^(([01]?[0-9]|2[0-3]):[0-5][0-9]|off)$";
                    p["default"] = key.Default;
                    break;
                default:
                    p["type"] = "string";
                    if (key.Required is null)
                        p["default"] = key.Default;
                    else
                        p["pattern"] = key.Pattern;
                    if (key.Choices != null)
                        p["enum"] = new JsonArray(key.Choices.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
                    break;
            }
            node[parts[^1]] = p;
            if (key.Required != null)
            {
                var section = root;                                 // the key, and every section on the way to it
                foreach (var part in parts)
                {
                    Need(section, part);
                    section = (JsonObject)section["properties"]![part]!;
                }
            }
        }
        foreach (var (_, section) in props)
            if (section is JsonObject o && o["type"]?.GetValue<string>() == "object")
                Close(o);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, NewLine = "\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    /// <summary>docs/settings.md's tables of keys, one per section, made from <see cref="Settings.Keys"/>: what the self-test compares the committed
    /// tables with.</summary>
    public static string KeysMarkdown()
    {
        var sb = new StringBuilder();
        foreach (var group in Settings.Keys.GroupBy(k => k.Path.Split('.')[0]))
        {
            sb.Append("\n### `").Append(group.Key).Append("`\n\n| Key | Type | Default | Read by | Replaces | What |\n|---|---|---|---|---|---|\n");
            foreach (var k in group)
            {
                var readers = string.Join(", ", new[] { (Part.Bridge, "bridge"), (Part.Plugin, "plugin"), (Part.Server, "server"), (Part.Sync, "sync") }
                    .Where(r => (k.Readers & r.Item1) != 0).Select(r => r.Item2));
                var type = k.Choices != null ? string.Join(" \\| ", k.Choices) : k.Type + (k.Min is { } lo ? $", {lo.ToString(CultureInfo.InvariantCulture)}..{k.Max!.Value.ToString(CultureInfo.InvariantCulture)}" : "");
                sb.Append("| `").Append(k.Path).Append("` | ").Append(type).Append(" | ").Append(k.Required != null ? "(required)" : k.Default.Length == 0 ? "(empty)" : "`" + k.Default.Replace("|", "\\|") + "`")
                  .Append(" | ").Append(readers)
                  .Append(" | ").Append(k.Replaces).Append(" | ").Append(k.Doc.Replace("|", "\\|")).Append(k.Secret ? " A secret: write it as a reference." : "").Append(" |\n");
            }
        }
        return sb.ToString();
    }

    /// <summary>One more name in an object's "required" list of the schema, once.</summary>
    static void Need(JsonObject section, string name)
    {
        if (section["required"] is not JsonArray list)
            section["required"] = list = [];
        if (!list.Any(n => n?.GetValue<string>() == name))
            list.Add((JsonNode?)JsonValue.Create(name));
    }

    static void Close(JsonObject section)
    {
        section["additionalProperties"] = false;
        foreach (var (_, inner) in (JsonObject)section["properties"]!)
            if (inner is JsonObject o && o["type"]?.GetValue<string>() == "object")
                Close(o);
    }
}
