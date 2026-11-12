// --selftest's shared tools. The harness is the one fake world for every section that plays the bridge through time: a clock that moves
// only when told, the bridge's timers on that clock, a fake game server that answers RCON (every command recorded), a scratch state
// directory, log lines in the HTTP push form and the players who join, leave and talk. ResetAll puts every static of the bridge back to a
// bridge that has just started on the run's own lists, so no section leaks into the next; StaticsSelfTest holds it to every static there is.
// And the live state as the plugin has it, and the sync's report as it sends one.
using System.Reflection;

static partial class Program
{
    /// <summary>The run's own lists, as SelfTestRun loaded them (maps.json): what every reset puts back.</summary>
    static List<MapInfo> SelfTestMaps = new();

    /// <summary>Every static of the bridge back to a bridge that has just started on the run's own lists - each named once, here. Kept as they are:
    /// the configuration (but the two modes a test may switch, back to the settings'), the doors a test fakes (Send, After, Now, HudActive,
    /// HudHeld: the harness's), the event loop, the run's own switches, and the versions that only count up (the plugin refetches on any move).</summary>
    static void ResetAll()
    {
        // the lists, as the run loaded them, and what is worked out of them
        Maps = SelfTestMaps; ModeMaps.Clear(); Broken = new(); BrokenReasons = new(); NonMaps = new(); BlockedOwn = new(); BlockedInBroken = new(); Blocked = new();
        WorkshopCheck = new(); Removed = new(); Sizes = new(); SizeBounds = default; Ours = new(); Search = new(); SearchText = Search.Encode();
        RatingScores = new(); RatingTrend = new(); RatingsFetched = 0; WorkshopInfo = new(); NumbersStamp++;
        knownFor = null; knownByKey = new(); updatedByKey = new(); popularityFor = null; popularity.Clear(); keptFor = null; kept = new(); wingmanIndex = (null, new());
        CatalogVersion++; // the menu's map list is made again
        // what the bridge keeps (state.json, votes.json), and who is on
        S = new State(); Likes = new(); Players.Clear(); PickConfirms.Clear(); HudSeen.Clear(); WhyOpen.Clear(); RefusedSaid.Clear();
        // the map change, the vote, the server's life, the crash watch, the empty server, the modes, the restarts
        Pending = null; LoadPending = false; LoadUntil = default; LoadSentAt = default; LoadBehindDownload = false; MatchEndUntil = default; EndScreenAt = null;
        Ballot = null; LastRound = 0; VoteWaitUntil = default;
        StartRollOwed = false; StartRollNeedsProof = false; StartMapAt = default; StartRollRetryAt = default; LastNamedMapStart = default; MaintenanceOn = false;
        ResetCrashWatch(); BeforeQuit = null; enteredOnThisMap.Clear(); timedOutOnThisMap.Clear();
        NobodySince = null; ResetDone = false; ResetProbeAt = default;
        ModeBeforeChange = null; ModeProbing = false; ModeProbeAt = default; ModeVerifyDue = false; ModeReloads = 0; ModeResetRetryAt = default;
        RestartDue = null; RematchDue = null; FairNames.Clear(); FairSentAt = DateTime.MinValue; UpdatedNotice = ""; UpdatedNoticeUntil = default;
        RestartConfirmBy = ""; RestartConfirmUntil = default; RestartCooldownUntil = default; RematchConfirmBy = ""; RematchConfirmUntil = default; RematchWaitUntil = default; PicksDue.Clear();
        // the stock and the watcher's report, the data files' watch, the Sync page, the plugin's polls, the messages
        StockSize = BridgeConfig.Values.DiscoveryStock; ReadyIds = new(); CoveredIds = new(); ReadyAt = default; ExportedIds = new(); QueuesWritten = ""; StockTickAt = default; Background = null;
        WatchStop();
        SyncDoc?.Dispose(); SyncDoc = null; SyncStamp = null; SyncReadAt = default; SyncProblem = ""; dashBody = ""; webWritten = ""; webPage = ""; webWrittenAt = default; syncTicks = 0;
        hudPolledTicks = 0; hudHeldTicks = 0;
        ClearNotices();
        // the two settings a test may switch
        HudTabMode = HudTab.Parse(BridgeConfig.Values.Tab); HudCheckMode = HudCheck.Parse(BridgeConfig.Values.Check);
    }

    /// <summary>The statics ResetAll keeps, by name: the configuration, the doors, the event loop, the run's own switches and tables, the versions
    /// that only count up, the served texts that go with them, and the HTTP thread's counters.</summary>
    static readonly string[] KeptByReset =
    [
        nameof(StateDir), nameof(MapsFile), nameof(BrokenFile), nameof(Officials), nameof(ModeMapsDir),
        nameof(Send), nameof(After), nameof(Now), nameof(HudActive), nameof(HudHeld), nameof(Rc), nameof(Loop), nameof(Signals), nameof(Clock), nameof(Dice),
        nameof(SelfTesting), nameof(SyncOn), nameof(DashZone), nameof(ModeProbeEnabled), nameof(ToldAll), nameof(SelfTestMaps), nameof(FakeColours),
        nameof(CatalogText), nameof(catalogBuilt), nameof(DashText), nameof(DashVersion), nameof(SaySeq), nameof(HudTextNow), nameof(HudTextVersion), nameof(HudLock), nameof(HudWaiting),
        nameof(logPosts), nameof(lastLogTicks), nameof(Files),
    ];

    /// <summary>Every static field of the bridge that can change is named once: reset by ResetAll - itself, or by a helper it calls (ResetCrashWatch,
    /// WatchStop, ClearNotices) - or kept by name (KeptByReset); so a static added later without a reset fails here, not by a section that leaks
    /// into the next. A readonly table (an array, a string, a value) cannot change and needs no name. Reads the bridge's own source (a run from
    /// bridge/); the image has none.</summary>
    static void StaticsSelfTest(Action<string, bool> Check)
    {
        if (!File.Exists(Path.Combine("Tests", "SelfTestHarness.cs"))) { Console.WriteLine("skip the statics check: no bridge source here"); return; }
        var sources = Directory.GetFiles(".", "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();
        string BodyOf(string method) => sources.Select(t => (Text: t, At: t.IndexOf($"static void {method}()", StringComparison.Ordinal))).Where(x => x.At >= 0)
            .Select(x => x.Text[x.At..x.Text.IndexOf("\n    }", x.At, StringComparison.Ordinal)]).FirstOrDefault() ?? "";
        var body = BodyOf(nameof(ResetAll));
        foreach (System.Text.RegularExpressions.Match call in System.Text.RegularExpressions.Regex.Matches(body, @"(?<![\w.])([A-Z]\w*)\(\);")) body += BodyOf(call.Groups[1].Value);
        var fields = typeof(Program).GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsLiteral && !f.Name.Contains('<') && !(f.IsInitOnly && (f.FieldType.IsArray || f.FieldType == typeof(string) || f.FieldType.IsValueType)))
            .Select(f => f.Name).ToList();
        var reset = fields.Where(f => System.Text.RegularExpressions.Regex.IsMatch(body, $@"(?<![\w.]){f}\s*(=(?!=)|\+\+|\.Clear\(\))")).ToList();
        var unnamed = fields.Where(f => !reset.Contains(f) && !KeptByReset.Contains(f)).ToList();
        var both = reset.Where(KeptByReset.Contains).ToList();
        Check($"Every one of the bridge's {fields.Count} static fields that can change is reset by ResetAll ({reset.Count}) or kept by name ({KeptByReset.Length}), never both: "
            + (unnamed.Count + both.Count == 0 ? "so none leaks from one section into the next" : $"unnamed {string.Join(", ", unnamed)}; both {string.Join(", ", both)}"),
            fields.Count > 80 && unnamed.Count == 0 && both.Count == 0 && KeptByReset.All(fields.Contains));
    }

    /// <summary>A scratch directory of the run's own, beside the binary - else in the temporary folder (the image's /app is not writable).</summary>
    static string ScratchDir(string name)
    {
        try { var d = Path.Combine(AppContext.BaseDirectory, $"selftest-{name}-{Environment.ProcessId}"); Directory.CreateDirectory(d); return d; }
        catch (Exception) { var d = Path.Combine(Path.GetTempPath(), $"os2-bridge-{name}-{Environment.ProcessId}"); Directory.CreateDirectory(d); return d; }
    }

    /// <summary>The fake world a section plays the bridge in: its clock, the bridge's timers on it, a fake game server (Server answers every RCON
    /// command, after Sent records it), a scratch state directory, and the log lines a server and its players write. Disposing it puts the doors
    /// and the state directory back, resets every static and removes the directory.</summary>
    sealed class Harness : IDisposable
    {
        public DateTime Clock;
        public readonly List<string> Sent = new();
        public Func<string, string?> Server = _ => "";   // the fake server's answer to a command
        public Action<string>? OnLine;                   // what the fake server does when it writes a log line (a map load, counted)
        public readonly string Dir;
        public Func<bool> BridgeHudActive => saved.HudActive; // the bridge's own HudActive, which a section's fakes stand in for
        readonly List<(DateTime Due, long Seq, Action Work)> timers = new();
        long seq;
        readonly (Func<string, string?> Send, Action<int, Action> After, Func<DateTime> Now, string StateDir, Func<bool> HudActive, Func<bool> HudHeld) saved;

        public Harness(string name, DateTime start)
        {
            saved = (Program.Send, Program.After, Program.Now, StateDir, HudActive, HudHeld);
            Dir = ScratchDir(name);
            Clock = start;
            Program.Now = () => Clock;
            Program.After = (ms, work) => timers.Add((Clock.AddMilliseconds(ms), seq++, work));
            Program.Send = command => { Sent.Add(command); return Server(command); };
            StateDir = Dir;
            ResetAll();
        }

        /// <summary>A section starts: every static back (ResetAll), no timer due, nothing sent, no maintenance flag, an hour later.</summary>
        public void Fresh()
        {
            ResetAll();
            timers.Clear(); Sent.Clear();
            if (File.Exists(MaintenanceFile)) File.Delete(MaintenanceFile);
            InMaintenance();
            Clock = Clock.AddHours(1);
        }

        /// <summary>The clock moves a second at a time, every timer that falls due fires, then the second's tick - and after it, as the event
        /// loop does after every piece of work, the menu's list and the published state (AfterWork).</summary>
        public void Run(double seconds)
        {
            var end = Clock.AddSeconds(seconds);
            while (Clock < end)
            {
                var step = Clock.AddSeconds(1) < end ? Clock.AddSeconds(1) : end;
                while (timers.Where(t => t.Due <= step).OrderBy(t => t.Due).ThenBy(t => t.Seq).FirstOrDefault() is { Work: not null } t)
                {
                    timers.Remove(t);
                    if (t.Due > Clock) Clock = t.Due;
                    t.Work();
                }
                Clock = step;
                Tick(); AfterWork();
            }
        }

        /// <summary>A log line as the server pushes it, at this moment.</summary>
        public void Line(string ev) { OnLine?.Invoke(ev); Ingest($"{Clock:MM/dd/yyyy - HH:mm:ss}.000 - {ev}", null); }

        public void Join((string Name, string Id) p) { Line($"\"{p.Name}<2><{p.Id}><>\" connected, address \"192.0.2.1:27005\""); Line($"\"{p.Name}<2><{p.Id}><>\" entered the game"); }

        public void Leave((string Name, string Id) p, string reason = "NETWORK_DISCONNECT_DISCONNECT_BY_USER") => Line($"\"{p.Name}<2><{p.Id}><CT>\" disconnected (reason \"{reason}\")");

        public void MatchEnd() => Line("Game Over: gungameprogressive mg_armsrace somemap score 0:0 after 9 min");

        /// <summary>A player says something in chat (presence only), and then does it - what a click in the menu asks the bridge for, in the
        /// same words the chat commands had.</summary>
        public void Talk((string Name, string Id) p, string text) { Line($"\"{p.Name}<2><{p.Id}><CT>\" say \"{text}\""); Command(p.Name, p.Id, text); }

        /// <summary>The countdown runs out, the server logs everyone out with reason SHUTDOWN, loads the map (named so once the countdown is over),
        /// and everyone reconnects.</summary>
        public void Land(Func<string> map, double settle = 0)
        {
            Run(ChangeDelay);
            var back = Players.Where(kv => kv.Value.GoneSince == null).Select(kv => (kv.Value.Name, kv.Key)).ToList();
            foreach (var p in back) Leave(p, "NETWORK_DISCONNECT_SHUTDOWN");
            Line($"Loading map \"{map()}\""); Line("Started:  \"\"");
            foreach (var p in back) Join(p);
            if (settle > 0) Run(settle);
        }

        /// <summary>A note or a line for everybody said it (Notices.cs).</summary>
        public bool Said(string fragment) => Told.Any(t => t.Contains(fragment, StringComparison.Ordinal));

        /// <summary>Nothing sent, nothing said: what comes next is the test's own.</summary>
        public void Quiet() { Sent.Clear(); ClearNotices(); }

        public void Dispose()
        {
            (Program.Send, Program.After, Program.Now, StateDir, HudActive, HudHeld) = saved;
            ResetAll();
            try { Directory.Delete(Dir, true); } catch (Exception e) { Console.WriteLine($"selftest: could not remove {Dir}: {e.Message}"); }
        }
    }

    /// <summary>The live state as the bridge publishes it now (HudTransport.cs): what the plugin's next poll is answered with.</summary>
    static string Published()
    {
        PublishHud();
        lock (HudLock) return HudTextNow;
    }

    /// <summary>The live state as the plugin has it at this moment: the published text, its end times counted down to now (HudState.AsOf).</summary>
    static HudState LiveHud() => HudState.Decode(Published())!.AsOf(HudState.Ms(Now()));

    /// <summary>The vote as the plugin has it at this moment (VoteState.AsOf).</summary>
    static VoteState LiveVote() => VoteState.Decode(Published()).AsOf(HudState.Ms(Now()));

    /// <summary>The Workshop sync's report of these verdicts, as it posts it (core's BridgeWire.RemovedBody).</summary>
    static string RemovedBody(IEnumerable<(long Id, string Verdict)> verdicts) => BridgeWire.RemovedBody(verdicts.Select(v => (v.Id.ToString(), v.Verdict)));
}
