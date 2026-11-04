// --selftest, the random queues (RandomQueue.cs, the owner's request of 2026-11-03): rolled ahead with the dice of a random pick, one queue
// per list, taken on disk first, re-checked when used, consumed by the vote whether shown or chosen, kept in state.json and exported to
// queues.json for the restart watcher. Self-contained: its own lists, clock, state directory and RCON stub, all put back afterwards.
using System.Text.Json;

static partial class Program
{
    static void RandomQueueSelfTest(Action<string, bool> Check)
    {
        var saved = (Maps, S, Broken, Packs, Votes, Removed, Blocked, Sizes, Send, Now, StateDir, ReadyIds, ReadyAt, HudActive, VoteCapable);
        var savedModes = ModeMaps.ToDictionary(kv => kv.Key, kv => kv.Value);
        string dir;
        try { dir = Path.Combine(AppContext.BaseDirectory, $"selftest-rq-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        catch (Exception) { dir = Path.Combine(Path.GetTempPath(), $"gungamebridge-rq-{Environment.ProcessId}"); Directory.CreateDirectory(dir); }
        try
        {
            var clock = new DateTime(2026, 11, 3, 18, 0, 0, DateTimeKind.Utc);
            Now = () => clock;
            StateDir = dir;
            Send = _ => "";
            HudActive = () => false;
            void Fresh()
            {
                S = new State(); Broken = new(); Packs = new(); Votes = new(); Removed = new(); Blocked = new(); Sizes = new();
                ReadyIds = new(); ReadyAt = default; Queue.Clear(); Pending = null; AnnouncePending = false; Ballot = null; QueuesWritten = "";
                Maps = Enumerable.Range(1, 1100).Select(r => new MapInfo(r, 8_000_000 + r, $"q_{r}", 10)).ToList();
                ModeMaps.Clear();
                ModeMaps[Modes.DeathmatchId] = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 8_100_000 + r, $"dm_{r}", 10, false, Modes.DeathmatchId)).ToList();
                ModeMaps[Modes.WingmanId] = Enumerable.Range(1, 60).Select(r => new MapInfo(r, 8_200_000 + r, $"wm_{r}", 10, false, Modes.WingmanId)).ToList();
            }
            string Head(string q) => QueueOf(q)[0].Key;

            Fresh();
            FillQueues();
            Check($"Queues: four, each {QueueLength} long, no map twice in one",
                QueueNames.All(n => QueueOf(n).Count == QueueLength && QueueOf(n).Select(e => e.Key).Distinct().Count() == QueueLength));
            Check("Queues: the new maps' queue holds only new maps (#1017 and up), the original queue only #1-#1016, each mode's only its pool",
                QueueOf(QueueNew).All(e => Maps.Single(m => Key(m) == e.Key).Rank >= FirstNewRank)
                && QueueOf(QueueOriginal).All(e => Maps.Single(m => Key(m) == e.Key).Rank < FirstNewRank)
                && QueueOf(Modes.DeathmatchId).All(e => PoolOf(Modes.Deathmatch).Any(m => Key(m) == e.Key))
                && QueueOf(Modes.WingmanId).All(e => PoolOf(Modes.Wingman).Any(m => Key(m) == e.Key)));
            var file = JsonSerializer.Deserialize(File.ReadAllText(QueuesPath), Ctx.Default.QueuesFile)!;
            Check("queues.json: version 1, the four queues in play order, the length, a time",
                file.Version == 1 && file.Length == QueueLength && QueueNames.All(n => file.Random[n].SequenceEqual(QueueOf(n).Select(e => e.Key))) && file.Written.EndsWith('Z'));
            var text = JsonSerializer.Serialize(S, Ctx.Default.State);
            var back = JsonSerializer.Deserialize(text, Ctx.Default.State)!;
            Check("The queues live in state.json and come back as they were (a bridge restart keeps them)",
                QueueNames.All(n => back.RandomQueues[n].SequenceEqual(S.RandomQueues[n])) && text.Contains("\"randomQueues\""));

            // ---- taking: the head, on disk first, refilled at once ----
            var head = Head(QueueNew);
            var pick = RandomNext(true, 1);
            Check("No report from the watcher: a pick takes the head of the new maps' queue (new maps always), and the queue rolls its next at once",
                pick != null && Key(pick) == head && QueueOf(QueueNew).Count == QueueLength && QueueOf(QueueNew).SkipLast(1).All(e => e.Key != head) && RollNote == ""); // the new last entry is a fresh roll, which may be any map
            Check("No pick for an empty server or with the dice switched off, and nothing is taken then",
                RandomNext(true, 0) is null && RandomNext(false, 3) is null && QueueOf(QueueNew).Count == QueueLength);
            var third = QueueOf(QueueNew)[2].Key;
            Check("A watcher's report: refused unless it is gg-ready 1, else every id it names", ApplyReady("hello\n1\n").StartsWith("refused") && ApplyReady($"gg-ready\t1\n{third}\nnot-an-id\n").StartsWith("ok: 1 on disk"));
            var (first, second) = (Head(QueueNew), QueueOf(QueueNew)[1].Key);
            pick = RandomNext(true, 1);
            Check("With a fresh report a pick takes the first entry on disk, and the two before it stay, in their places",
                pick != null && Key(pick) == third && QueueOf(QueueNew)[0].Key == first && QueueOf(QueueNew)[1].Key == second);
            ApplyReady("gg-ready\t1\n"); // a fresh report that names no queued map (the refill may have rolled the one it named again)
            pick = RandomNext(true, 1);
            Check("Nothing on disk: the first eligible entry anyway (the server downloads it, as it always did)", pick != null && Key(pick) == first);
            Check("The report's answer says hold while a map change counts down or loads, or a vote is open, and nothing else",
                !ApplyReady("gg-ready\t1\n").Contains("hold") && (Pending = new Change(Maps[0], "test", [])) != null && ApplyReady("gg-ready\t1\n").EndsWith("; hold: a map change is counting down")
                && (Pending = null) == null && (AnnouncePending = true) && ApplyReady("gg-ready\t1\n").EndsWith("; hold: a map is loading") && !(AnnouncePending = false));
            clock = clock.AddMinutes(ReadyFreshMinutes + 1);
            Check("A report older than ReadyFreshMinutes counts for nothing: every entry is on disk again", !ReadyKnown && IsReady(Maps[1050]));
            var (stale, young, onDisk) = (QueueOf(QueueNew)[0], QueueOf(QueueNew)[1], QueueOf(QueueNew)[2]);
            ApplyReady($"gg-ready\t1\n{onDisk.Key}\n");
            QueueOf(QueueNew)[0] = stale with { At = clock.AddHours(-NotReadyDropHours - 1) };
            pick = RandomNext(true, 1);
            Check($"An entry still not on disk after {NotReadyDropHours} h, while the reports keep coming, is dropped; a younger one waits its turn",
                pick != null && Key(pick) == onDisk.Key && QueueOf(QueueNew).SkipLast(2).All(e => e.Key != stale.Key) && QueueOf(QueueNew)[0].Key == young.Key); // the last two are fresh rolls (one for the dropped entry, one for the pick), which may be any map
            ReadyAt = default;

            // ---- re-checked when used ----
            head = Head(QueueNew);
            Votes[head] = new() { ["[U:1:5]"] = AdsVote };
            pick = RandomNext(true, 1);
            Check("An entry a player marked as showing ads since its roll is dropped, never played", pick != null && Key(pick) != head && QueueOf(QueueNew).All(e => e.Key != head));
            head = Head(QueueNew);
            S.Crashes[head] = 1;
            pick = RandomNext(true, 1);
            Check("An entry that crashed the server since its roll is dropped", pick != null && Key(pick) != head);
            head = Head(QueueNew);
            S.History.Add(Maps.Single(m => Key(m) == head));
            pick = RandomNext(true, 1);
            Check("An entry played since its roll (a request) is dropped", pick != null && Key(pick) != head);
            head = Head(QueueNew);
            Sizes[head] = new SizeEntry(20000, "XL", "nav");
            pick = RandomNext(true, 1);
            Check("An entry whose too-big mark changed since its roll is dropped: the 1-in-100 group was not what rolled it", pick != null && Key(pick) != head);
            Sizes = new();
            int kept = 0, trials = 4000;
            for (var i = 0; i < trials; i++)
            {
                Fresh(); FillQueues();
                head = Head(QueueNew);
                Votes[head] = new() { ["[U:1:6]"] = DownReasons.Plain }; // a downvote: a quarter of the odds
                if (RandomNext(true, 1) is { } p && Key(p) == head) kept++;
            }
            Check($"A downvote since its roll keeps an entry with the odds it left it: {kept} of {trials} kept, a quarter expected", kept is > 880 and < 1120);

            // ---- the new-maps setting picks the queue, and needs no new rolls ----
            Fresh(); FillQueues();
            var before = QueueOf(QueueOriginal).Select(e => e.Key).ToList();
            S.NewMaps = NewMapsNever;
            pick = RandomNext(true, 1);
            Check("Never: the original list's queue, and the new maps' queue is left as it was", pick != null && Key(pick) == before[0] && pick.Rank < FirstNewRank);
            S.NewMaps = NewMapsHalf;
            var newer = 0;
            for (var i = 0; i < 2000; i++) if (RandomNext(true, 1) is { } p && p.Rank >= FirstNewRank) newer++;
            Check($"Half: a fair coin between the two queues - {newer} of 2000 new maps", newer is > 900 and < 1100);
            S.NewMaps = NewMapsAlways;
            Broken = Maps.Where(IsNew).ToDictionary(Key, _ => 3);
            pick = RandomNext(true, 1);
            Check("Always, with no new map eligible: the original list's queue, and the note RandomPick gives", pick != null && pick.Rank < FirstNewRank && RollNote == NoNewMapsNote);
            Broken = new();

            // ---- the other modes, and the vote ----
            Fresh(); FillQueues();
            head = Head(Modes.DeathmatchId);
            pick = RandomNext(true, 1, Modes.Deathmatch);
            Check("Deathmatch: its own queue's head", pick != null && Key(pick) == head && pick.Mode == Modes.DeathmatchId);
            var shown = new HashSet<string>(QueueNames.SelectMany(n => QueueOf(n).Take(MapVote.MinMaps).Select(e => e.Key)));
            Check($"The vote opens with its {MapVote.MinMaps} maps from the queues", OpenBallot(clock, 2) && Ballot is { } b && b.Options.Count(o => o.Source == VoteSource.Rolled) == MapVote.MinMaps);
            // the queue each option came from: its mode's (Arms Race's new maps under always); a map on two modes' lists (de_overpass is
            // Deathmatch's and Wingman's) has an entry of each mode's own dice, and only the one taken is used up
            static string From(BallotOption o) => o.Mode.ArmsRace ? QueueNew : o.Mode.Id;
            var rolled = Ballot!.Options.Where(o => o.Map != null).ToList();
            var takes = rolled.GroupBy(From).ToDictionary(g => g.Key, g => g.Count()); // each take is rolled anew at the end of its queue, and a fresh roll may be any map
            Check("Every map the ballot shows was its queue's first entry, and is gone from that queue - shown or chosen, it was used, so it comes back no sooner than the dice bring it",
                rolled.All(o => shown.Contains(Key(o.Map!))) && rolled.All(o => QueueOf(From(o)).SkipLast(takes[From(o)]).All(e => e.Key != Key(o.Map!)))
                && QueueNames.All(n => QueueOf(n).Count == QueueLength));
            Ballot = null;

            // ---- the requests go to queues.json too ----
            Queue.Add(new Request(Maps[5]));
            ExportQueues();
            file = JsonSerializer.Deserialize(File.ReadAllText(QueuesPath), Ctx.Default.QueuesFile)!;
            Check("queues.json names the maps players have queued, so the watcher fetches them before the vote", file.Requested.SequenceEqual([Key(Maps[5])]));
            Queue.Clear();
            Check("The health page says how many of each queue are on disk", QueueHealth().StartsWith($"random queues of {QueueLength}: ") && QueueHealth().Contains("no report from the restart watcher yet"));
        }
        finally
        {
            (Maps, S, Broken, Packs, Votes, Removed, Blocked, Sizes, Send, Now, StateDir, ReadyIds, ReadyAt, HudActive, VoteCapable) = saved;
            ModeMaps.Clear();
            foreach (var (k, v) in savedModes) ModeMaps[k] = v;
            Queue.Clear(); Ballot = null; Pending = null; QueuesWritten = "";
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }
}
