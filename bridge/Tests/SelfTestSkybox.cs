// --selftest, a Workshop item's 3D-skybox submap (Skybox.cs): a change to the item that is on goes by changelevel to its level, never host_workshop_map
// (which loaded cs_assault_skybox live on 2026-11-25), and falls back to the id when the server does not find the level; a map start of a skybox, the
// bridge's own load or an operator's, goes on to the item's level - the name without "_skybox", else the level its history logged - the map staying
// the item, picked as it was; and with no level the server takes, the dice. Played against the fake server, through the log lines a server writes.
static partial class Program
{
    static void SkyboxSelfTest(Action<string, bool> Check)
    {
        var h = new Harness("skybox", new DateTime(2026, 11, 25, 20, 0, 0, DateTimeKind.Utc));
        var alice = ("Alice", "[U:1:4201]");
        try
        {
            var items = Maps.Where(m => !m.Official && m.Id > 0 && m.Rank > 0).Take(3).ToList();
            var (item, other, third) = (items[0], items[1], items[2]);
            void Change(MapInfo map) => StartChange(new Change(map, "the vote"), "A test's change");
            string Joined() => string.Join(" | ", h.Sent);
            // what a map start sends besides (the warm-up's look, the mode's word to the plugin, status) aside: a mode's commands and the map commands
            List<string> MapCommands() => [.. h.Sent.Where(c => Modes.All.Any(m => c == Modes.PrepareLine(m)) || Rcon.ChangesMap(c))];

            Check("A level ending in _skybox is a 3D-skybox submap; the suffix alone, or one inside a name, is none",
                IsSkybox("cs_assault_skybox") && IsSkybox("CS_ASSAULT_SKYBOX") && !IsSkybox("_skybox") && !IsSkybox("cs_assault") && !IsSkybox("aim_skybox_arena")
                && LevelsOf("cs_assault_skybox", 0).SequenceEqual(["cs_assault"]));

            // ---- a mode change on the item that is on: changelevel to the level it is on, the mode's commands first ----
            h.Fresh();
            S.Mode = Modes.ArmsRaceId;
            h.Join(alice);
            h.Quiet();
            Change(item);
            var first = h.Sent.LastOrDefault();
            h.Land(() => "cs_level", settle: 3);
            Check($"A Workshop item from another map loads by its id, host_workshop_map, as ever ({first}; on {S.LogName})",
                first == $"host_workshop_map {item.Id}" && S.LogName == "cs_level" && S.Map == item && !LoadPending);
            var asDeathmatch = item with { Mode = Modes.DeathmatchId };
            h.Quiet();
            Change(asDeathmatch);
            Check($"The same item in another mode - a vote's card of it while it is on - goes by changelevel to the level it is on, after the mode's commands, never by its id: "
                  + $"host_workshop_map of the item that is on loaded its skybox submap live ({Joined()})",
                h.Sent.SequenceEqual([Modes.PrepareLine(Modes.Deathmatch), "changelevel cs_level"]) && LoadPending && S.Map == asDeathmatch && S.PickedBy == "the vote"
                && S.Mode == Modes.DeathmatchId);
            h.Land(() => "cs_level", settle: 3);
            Check("...and its map start is the change's own: the item, in Deathmatch, picked by the vote",
                S.LogName == "cs_level" && S.Map == asDeathmatch && S.PickedBy == "the vote" && !LoadPending && S.Mode == Modes.DeathmatchId);
            h.Quiet();
            h.Server = c => c == "changelevel cs_level" ? "changelevel cs_level:  invalid map name" : "";
            var asWingman = item with { Mode = Modes.WingmanId };
            Change(asWingman);
            Check($"...a level the server does not find by its name: by the item's id after all ({Joined()})",
                h.Sent.SequenceEqual([Modes.PrepareLine(Modes.Wingman), "changelevel cs_level", $"host_workshop_map {item.Id}"]) && LoadPending && S.Map == asWingman
                && !Unloadable.Contains("cs_level"));
            h.Server = _ => "";
            h.Land(() => "cs_level", settle: 3);
            h.Quiet();
            Change(other);
            Check($"Another item while one is on: by its id, as ever ({Joined()})", h.Sent[^1] == $"host_workshop_map {other.Id}");

            // ---- the bridge's own load comes up as the item's skybox: on to its level, the map staying the item ----
            h.Land(() => "cs_other_skybox");
            Check("A load that comes up as a skybox is the change's map start all the same: the map is the item, the level its skybox",
                S.Map == other && S.LogName == "cs_other_skybox" && !LoadPending);
            h.Quiet();
            h.Run(SkyboxOutMs / 1000.0 + 1);
            Check($"...and {SkyboxOutMs / 1000} s on it goes on to the item's level - the name without _skybox, the mode's commands first - awaited as the change's own, "
                  + $"and said ({Joined()})",
                MapCommands().SequenceEqual([Modes.PrepareLine(Modes.ArmsRace), "changelevel cs_other"]) && LoadPending && S.Map == other && S.PickedBy == "the vote"
                && h.Said($"{Clip(Label(other), 40)} came up as its skybox, where nobody can spawn - Loading cs_other instead"));
            h.Land(() => "cs_other", settle: 3);
            h.Quiet();
            h.Run(SkyboxOutMs / 1000.0 + 2);
            Check($"...whose map start is the item, picked by the vote, on its level - and nothing more goes out ({Joined()})",
                S.Map == other && S.LogName == "cs_other" && S.PickedBy == "the vote" && !LoadPending && !h.Sent.Any(c => c.StartsWith("changelevel", StringComparison.Ordinal)));

            // ---- the name without the suffix is not there: the level the same item logged at its last start ----
            h.Quiet();
            History.Entries.Insert(0, new HudHistory.Entry(new DateTimeOffset(h.Clock.AddDays(-1)), new DateTimeOffset(h.Clock.AddDays(-1).AddMinutes(20)), third.Id, third.Name,
                "dm_third_main", Modes.DeathmatchId, "match end", "", [], [], "live"));
            h.Server = c => c == "changelevel dm_third" ? "changelevel dm_third:  invalid map name" : "";
            Change(third);
            h.Land(() => "dm_third_skybox");
            h.Quiet();
            h.Run(SkyboxOutMs / 1000.0 + 1);
            Check($"A skybox whose name without the suffix the server does not find: the level the same item logged at its last start in the history ({Joined()})",
                MapCommands().SequenceEqual([Modes.PrepareLine(Modes.ArmsRace), "changelevel dm_third", "changelevel dm_third_main"]) && LoadPending && S.Map == third);
            h.Server = _ => "";
            h.Land(() => "dm_third_main", settle: 3);
            Check("...and that is where the item plays", S.Map == third && S.LogName == "dm_third_main" && !LoadPending);

            // ---- no level the server takes: the dice, nobody can play there ----
            h.Quiet();
            Change(item);
            h.Land(() => "solo_skybox");
            h.Quiet();
            h.Server = c => c.StartsWith("changelevel solo", StringComparison.Ordinal) ? $"{c}:  invalid map name" : "";
            h.Run(SkyboxOutMs / 1000.0 + 1);
            Check($"A skybox with no other level the server takes: the dice, or the word to start a vote - nobody can spawn there ({Joined()})",
                h.Sent.Contains("changelevel solo") && h.Said($"{Clip(Label(item), 40)} came up as its skybox, where nobody can spawn")
                && (Pending != null || LoadPending || h.Said("Start a vote for another map")));
            h.Server = _ => "";

            // ---- an operator's load the bridge did not ask for comes up as a skybox: on to its level, nothing awaited ----
            h.Fresh();
            S.Mode = Modes.ArmsRaceId;
            h.Join(alice);
            h.Quiet();
            h.Line("Loading map \"op_map_skybox\""); h.Line("Started:  \"\"");
            h.Run(SkyboxOutMs / 1000.0 + 1);
            Check($"An operator's load the bridge did not ask for that comes up as a skybox goes on to its level as well - no mode command, as the bridge does not know "
                  + $"the item, and nothing awaited ({Joined()})",
                MapCommands().SequenceEqual(["changelevel op_map"]) && !LoadPending && S.Map == null);
            h.Line("Loading map \"op_map\""); h.Line("Started:  \"\"");
            h.Quiet();
            h.Run(SkyboxOutMs / 1000.0 + 2);
            Check("...and the level it goes to stays", S.LogName == "op_map" && !h.Sent.Any(c => c.StartsWith("changelevel", StringComparison.Ordinal)));

            // ---- a skybox start that another map start follows within the wait: nothing goes out for it ----
            h.Quiet();
            h.Line("Loading map \"gone_skybox\""); h.Line("Started:  \"\"");
            h.Run(1);
            h.Line("Loading map \"next_map\""); h.Line("Started:  \"\"");
            h.Run(SkyboxOutMs / 1000.0 + 1);
            Check($"A skybox start another map start follows before its wait is over sends nothing for it ({Joined()})",
                !h.Sent.Contains("changelevel gone") && S.LogName == "next_map");
        }
        finally
        {
            h.Dispose();
        }
    }
}
