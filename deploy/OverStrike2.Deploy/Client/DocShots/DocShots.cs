using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>A message of the message list (messages.txt): its name, its bracket, where it goes, and the heading it is under.</summary>
sealed class Pick(string name, string bracket, string surfaces, string heading)
{
    public string Name { get; } = name;
    public string Bracket { get; } = bracket;
    public string Surfaces { get; } = surfaces;
    public string Heading { get; } = heading;
    public string Words { get; set; } = "";
    public bool Notes { get; set; }
}

/// <summary>What validate holds the table to, and what it found wrong.</summary>
sealed class Info
{
    public List<Pick> Picks { get; init; } = [];
    public Dictionary<string, Pick> ByName { get; init; } = [];
    public HashSet<string> Buttons { get; init; } = [];
    public HashSet<string> Named { get; init; } = [];
    public string Commit { get; init; } = "?";
    public string Content { get; init; } = "?";
    public string Plugin { get; init; } = "?";
    public List<string> Problems { get; init; } = [];
}

/// <summary>A row in a plan: shoot, or setup (its steps run for its state, no pictures); jump: the row before it in the plan is not its predecessor
/// in the table, so the window's state is not known.</summary>
sealed record Entry(Row Row, string Mode, bool Jump = false);

sealed class RowFailed(string message) : Exception(message);

/// <summary>tools/client/docshots/docshots.py, ported: the documentation screenshots of Over Strike 2's HUD, from a real client on a test copy
/// (AGENTS.md, "The documentation shows the whole HUD"). One table of rows (Table.cs): every page, tab and state of the HUD, and every message of the
/// message list (messages.txt beside docshots.py, or --picks &lt;file&gt; in its format). It drives cs2client.ps1's verbs rcon, cmd, shot and log and,
/// for what only the restart watcher or other players can send the bridge, this program's bridgepost, which refuses anything but a candidate bridge on
/// a test copy: it starts no client and no server, and it never reads a secret.
///
///     os2-deploy docshots --list                                   every row, how it is reached, and the counts per status
///     os2-deploy docshots --dry-run [--res WxH|all] [--only a,b]   exactly what a run would execute, touching nothing
///     os2-deploy docshots --selfcheck                              the offline checks, every resolution; touches no server, no client, no lock
///     os2-deploy docshots --res 1920x1080 --out &lt;folder&gt; [--only a,b] [--bridge-copy] [--renamed] [--keep-raw]     the real run, one resolution
///     os2-deploy docshots --res 1920x1080 --out &lt;folder&gt; --remask      the masks again on a --keep-raw run's raw frames: no game, no server
///     os2-deploy docshots --mask-file &lt;in.png&gt; &lt;out.png&gt; --row &lt;id&gt;    a row's masks on any picture, to check them offline
///     os2-deploy docshots publish | name | merge | maskview | sheets ...   publish.py, docname.py, merge_fix.py, maskview.py, review_sheet.py
///
/// docshots.py's docstring says the rest - the session, the timing, the names, the statuses - and holds for this port word for word.</summary>
static partial class DocShots
{
    // ---------------------------------------------------------------- the deployment and the committed code

    public static string Os2 => Repo.Root;                                                   // the repository: its HEAD is what the rows are held to
    public static string Here => Repo.P("tools", "client", "docshots");
    public static string HarnessPath => Repo.P("tools", "client", "cs2client.ps1");
    static string Work => Path.Combine(Harness.MainCheckout(), ".work", "docshots");         // scratch: the dry run's default folder, the self-check's
    const string Layout = "hud/layout/gungame_hud_v3_0_0.xml";
    static string OwnerLine => "owner: " + Harness.Owner;                                     // the harness's lock: its first line

    public static string PicksPath = "";      // the message list: every on-screen message the documentation shows
    public static string Host = "play.example.com";
    public static int Port = 27015;
    public static string? LockPath;
    public static string SteamAccountText = "";
    public static string[] RealNames = [];   // never in a command, a mask or a row's words; scrubbed from the run's log
    public static string Account = MadeUpAccount;
    public static string ContentName = "?";
    public static int Level;
    public static bool Renamed;              // --renamed: the client goes by Me on the server (GgDocName), so no mask is needed for a name
    public static string Built = "";         // --built: the commit the build under test came from, when HEAD is later by documentation only
    public static string? ConfigPath;        // --config: handed on to bridgepost
    public static List<Row> Rows = [];

    /// <summary>The deployment from the private config and this PC's Steam login, the committed content, and the table. Without a config: a made-up
    /// server and no lock, which the offline modes can work with and a real run refuses.</summary>
    public static void Init(string? configPath)
    {
        ConfigPath = configPath;
        var h = new Harness(configPath);
        if (h.HasConfig)
        {
            (Host, Port) = h.HostPort;
            var l = h.Cfg.Str("server.lock_file");
            LockPath = l.Length > 0 ? l : null;
        }
        else
        {
            (Host, Port, LockPath) = ("play.example.com", 27015, null);
        }
        var account = Harness.SteamAccount();
        SteamAccountText = account != 0 ? account.ToString(CultureInfo.InvariantCulture) : "";
        RealNames = h.PrivateNames().Select(n => n.ToLowerInvariant()).ToArray();
        Account = SteamAccountText.Length > 0 ? SteamAccountText : MadeUpAccount;
        PicksPath = Path.Combine(Here, "messages.txt");
        var contents = Regex.Matches(HeadFile("bridge/HudVersion.cs") ?? "", @"\(""(v[\d.]+)"", (\d+)\),");
        (ContentName, Level) = contents.Count > 0 ? (contents[^1].Groups[1].Value, int.Parse(contents[^1].Groups[2].Value, CultureInfo.InvariantCulture)) : ("?", 0);
        Rows = BuildRows();
    }

    static readonly Dictionary<string, string?> HeadCache = [];

    /// <summary>A file as committed in this repository (null when it is not there): someone may be editing the working tree.</summary>
    public static string? HeadFile(string path)
    {
        if (!HeadCache.TryGetValue(path, out var text))
        {
            var bytes = Repo.Show("HEAD", path);
            if (bytes is null && path.StartsWith("bridge/", StringComparison.Ordinal) && path.Count(c => c == '/') == 1)   // the bridge's shared files moved to bridge/Shared/
                bytes = Repo.Show("HEAD", "bridge/Shared/" + path[7..]);
            text = bytes is null ? null : Encoding.UTF8.GetString(bytes);
            HeadCache[path] = text;
        }
        return text;
    }

    /// <summary>Whether these words are still in the committed code: in that file, or anywhere under its top folder (a file split or moved keeps them).</summary>
    static bool WordsAtHead(string path, string words)
    {
        var source = HeadFile(path);
        if (source is not null && source.Contains(words, StringComparison.OrdinalIgnoreCase))
            return true;
        return Proc.Capture(["git", "-C", Os2, "grep", "-q", "-i", "-F", words, "HEAD", "--", path.Split('/')[0]]).Code == 0;
    }

    static string HeadCommit()
    {
        var r = Proc.Capture(["git", "-C", Os2, "rev-parse", "--short", "HEAD"]);
        return r.Code == 0 ? r.Out.Trim() : "?";
    }

    static string HeadVersion(string path, string pattern) => Regex.Match(HeadFile(path) ?? "", pattern) is { Success: true } m ? m.Groups[1].Value : "?";

    // ---------------------------------------------------------------- the picks file

    static List<Pick> ParsePicks(string text)
    {
        var m = Regex.Match(text, @"^MESSAGES \((\d+)\)\s*$", RegexOptions.Multiline);
        var end = text.IndexOf("WHAT CHANGES FROM TODAY", StringComparison.Ordinal);
        end = end < 0 ? text.Length : end;
        if (!m.Success || end < m.Index + m.Length)
            throw Out.Stop($"{PicksPath}: no MESSAGES (n) section");
        var declared = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var picks = new List<Pick>();
        var heading = "";
        foreach (var line in Py.SplitLines(text[(m.Index + m.Length)..end]))
        {
            var hit = Regex.Match(line, @"^- (.+?) \[(.+?)\]: (.*)$");
            if (hit.Success)
                picks.Add(new Pick(hit.Groups[1].Value, hit.Groups[2].Value, hit.Groups[3].Value, heading));
            else if (line.StartsWith("- ", StringComparison.Ordinal))
                throw Out.Stop($"{PicksPath}: a message line this script cannot read: {Py.Slice(line, 0, 80)}");
            else if (Regex.IsMatch(Py.Strip(line), @"^[A-Z][A-Z ,]+\z"))
                heading = Py.Strip(line);
            else if (line.StartsWith("  Words", StringComparison.Ordinal) && picks.Count > 0)
                picks[^1].Words = Py.Strip(line);
            else if (line.StartsWith("  OWNER'S NOTE", StringComparison.Ordinal) && picks.Count > 0)
                picks[^1].Notes = true;
        }
        if (picks.Count != declared)
            throw Out.Stop($"{PicksPath}: says {declared} messages, holds {picks.Count}");
        return picks;
    }

    static string SectionOf(Row row, Dictionary<string, Pick> byName)
    {
        if (row.Pick.Length > 0)
        {
            var heading = byName[row.Pick].Heading;
            return "Messages: " + heading[0] + heading[1..].ToLowerInvariant();
        }
        return row.Section;
    }

    // ---------------------------------------------------------------- the checks every invocation runs

    static readonly string[] PostVerbs = ["restart", "restart-cancel", "updated", "quit-notice", "fake-on", "fake-off", "fake-crash", "client-off", "client-on", "act", "vote", "names-check", "check"];
    static readonly string[] RconAllowed = ["ggd_name", "mp_restartgame 1", "sv_allow_votes", "css_gghud show ", "css_gghud press ", "css_gghud click ", "css_gghud level ", "css_gghud content ",
        "css_gghud centre ", "css_gghud release", "css_guide ", "gg_notice ", "gg_fun ", "os_catchup ", "sv_disable_radar", "os_mode ", "game_type ", "game_mode ", "changelevel "];
    static readonly string[] ChangelevelAllowed = ["ar_shoots", "ar_pool_day", "ar_shoots_night", "de_dust2", "de_inferno"];   // official maps: no download dialog
    static readonly Regex ConAllowed = new(@"^(say !?[\w !]+|say \d{7,12}|jointeam [123] 1|[+-]showscores|disconnect|connect \S+|status)$");
    static readonly Regex BridgeButtons = new(@"^(gg_b_next|gg_b_vup|gg_b_vdown|gg_btn_up|gg_btn_down|gg_m\d+_(play|up|down)|gg_why_\d+|gg_set_time_\d+|gg_v_random|gg_v_browse|gg_v_back)$");
    static readonly Regex BridgeSays = new(@"^say !(play|up|down|next|map)\b");
    static readonly string[] NeverButtons = ["gg_ws_play"];   // downloads to the server

    static bool RealNameIn(string text)
    {
        var low = text.ToLowerInvariant();
        return RealNames.Any(n => low.Contains(n, StringComparison.Ordinal));
    }

    static IEnumerable<Step> AllSteps(Row row) => row.Steps.Concat(row.Cleanup);

    static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Everything that can be held true offline: the picks, the button ids against the COMMITTED layout, the words the stand-ins use against
    /// the committed code, what the table may and may not send. A picks file that cannot be read stops here; everything else comes back in Problems.</summary>
    static Info Validate()
    {
        var errors = new List<string>();
        var picks = ParsePicks(File.ReadAllText(PicksPath, Encoding.UTF8));
        var byName = new Dictionary<string, Pick>();
        foreach (var p in picks)
            byName[p.Name] = p;
        if (byName.Count != picks.Count)
            errors.Add("the picks file names a message twice");
        foreach (var dup in Rows.GroupBy(r => r.Id).Where(g => g.Count() > 1))
            errors.Add($"row id twice: {dup.Key}");
        var byId = new Dictionary<string, Row>();
        foreach (var r in Rows)
            byId[r.Id] = r;
        var seenBefore = new HashSet<string>();
        foreach (var r in Rows)
        {
            if (!Regex.IsMatch(r.Id, @"^[a-z0-9][a-z0-9-]*\z"))
                errors.Add($"{r.Id}: not a file-safe id");
            if (!Statuses.Contains(r.Status))
                errors.Add($"{r.Id}: status {r.Status}");
            if (r.Pick.Length > 0 && !byName.ContainsKey(r.Pick))
                errors.Add($"{r.Id}: pick {Py.Repr(r.Pick)} is not in the picks file");
            if (r.Pick.Length == 0 && !Sections.Contains(r.Section))
                errors.Add($"{r.Id}: no pick and no known section");
            if (r.After.Length > 0 && !seenBefore.Contains(r.After))
                errors.Add($"{r.Id}: after {r.After}, which does not come before it");
            seenBefore.Add(r.Id);
            if (r.Status == Ready)
            {
                if (!r.Steps.Any(s => s.Kind == "shoot"))
                    errors.Add($"{r.Id}: ready, and takes no shot");
                if (r.When.Length == 0)
                    errors.Add($"{r.Id}: ready, and no word on when it is shot");
                if (r.Names.StartsWith("real name", StringComparison.Ordinal) && !(r.Masks.Length > 0 || r.Chat || r.Renamed))
                    errors.Add($"{r.Id}: would show a real name, and has no mask");
                if (r.After.Length > 0 && byId[r.After].Status != Ready)
                    errors.Add($"{r.Id}: after {r.After}, which is not ready");
                if (r.After.Length > 0 && byId[r.After].Bridge.Length > 0 && r.Bridge.Length == 0)
                    errors.Add($"{r.Id}: after {r.After}, which needs --bridge-copy, and does not say so itself");
            }
            else
            {
                if (r.Reason.Length == 0)
                    errors.Add($"{r.Id}: {r.Status} without a reason");
                if (r.Steps.Count > 0)
                    errors.Add($"{r.Id}: {r.Status} with steps");
            }
        }
        foreach (var p in picks.Where(p => !Rows.Any(r => r.Pick == p.Name)))
            errors.Add($"a pick without a row: {p.Name}");

        var layout = HeadFile(Layout) ?? throw Out.Stop($"git show HEAD:{Layout} failed: the button ids cannot be checked against the committed layout");
        var buttons = Regex.Matches(layout, @"<Button\b[^>]*?\bid=""([^""]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        var named = new HashSet<string>();
        foreach (var r in Rows)
        {
            var restarts = 0;
            foreach (var s in AllSteps(r))
            {
                if (s.Button.Length > 0)
                {
                    named.Add(s.Button);
                    if (!buttons.Contains(s.Button))
                        errors.Add($"{r.Id}: button {s.Button} is not in the committed layout");
                    if (NeverButtons.Contains(s.Button))
                        errors.Add($"{r.Id}: {s.Button} leaves state on the server");
                    if (BridgeButtons.IsMatch(s.Button) && r.Bridge.Length == 0)
                        errors.Add($"{r.Id}: {s.Button} writes the bridge's data or opens a vote, and the row does not say so");
                    if (s.Button == "gg_set_restart")
                        restarts++;
                }
                if (s.Kind is "rcon" or "rcon_bg")
                {
                    if (!RconAllowed.Any(a => s.Text.StartsWith(a, StringComparison.Ordinal)))
                        errors.Add($"{r.Id}: an RCON command outside the allowed set: {Py.Slice(s.Text, 0, 60)}");
                    if (s.Text.StartsWith("changelevel ", StringComparison.Ordinal) && !ChangelevelAllowed.Contains(Words(s.Text)[1]))
                        errors.Add($"{r.Id}: changelevel to {Words(s.Text)[1]}");
                    if (s.Text.Length > 500)
                        errors.Add($"{r.Id}: an RCON line of {s.Text.Length} characters: one line carries about 512 (HARNESS.md)");
                    var hit = Regex.Match(s.Text, @"^gg_notice \d+ ""(.*)""$");
                    if (s.Text.StartsWith("gg_notice", StringComparison.Ordinal) && (!hit.Success || hit.Groups[1].Value.Contains('"') || hit.Groups[1].Value.Length > 240))
                        errors.Add($"{r.Id}: a gg_notice line the plugin would cut or misread");
                }
                if (s.Kind == "con")
                {
                    if (!ConAllowed.IsMatch(s.Text))
                        errors.Add($"{r.Id}: a console command outside the allowed set: {Py.Slice(s.Text, 0, 60)}");
                    if (BridgeSays.IsMatch(s.Text) && r.Bridge.Length == 0)
                        errors.Add($"{r.Id}: {s.Text} writes the bridge's data, and the row does not say so");
                }
                if (s.Kind == "post")
                {
                    if (!PostVerbs.Contains(Words(s.Text)[0]))
                        errors.Add($"{r.Id}: bridgepost has no verb {Words(s.Text)[0]}");
                    if (r.Bridge.Length == 0)
                        errors.Add($"{r.Id}: a post, which only a candidate bridge may hear, and the row does not say so");
                }
                if (RealNameIn(s.Text))
                    errors.Add($"{r.Id}: a real name in a command");
            }
            foreach (var m in r.Masks)
                if (RealNameIn(m.Text))
                    errors.Add($"{r.Id}: a real name in a mask");
            if (RealNameIn(r.Shows + r.When + r.Reason + r.Today + r.Names))
                errors.Add($"{r.Id}: a real name in the row's words");
            var later = r.Cleanup.Where(x => x.Kind == "post").Select(x => Words(x.Text)).ToList();
            bool LaterHas(params string[] words) => later.Any(l => l.SequenceEqual(words));
            foreach (var said in r.Steps.Where(x => x.Kind == "post").Select(x => Words(x.Text)))
            {
                if (said[0] == "restart" && !LaterHas("restart-cancel", said[1]))
                    errors.Add($"{r.Id}: a restart announced, and no restart-cancel {said[1]} in its cleanup: no vote can start after it");
                if (said[0] == "fake-on" && !LaterHas("fake-off") && !Rows.Any(x => x.After == r.Id))
                    errors.Add($"{r.Id}: fake players on, and no fake-off in its cleanup or a row after it");
                if (said[0] == "act" && said[2] == "restart" && !LaterHas("quit-notice"))
                    errors.Add($"{r.Id}: a player's restart, and no quit-notice in its cleanup: the second click quits the server");
            }
            if (restarts > 1)
                errors.Add($"{r.Id}: gg_set_restart twice: the second click quits the server");
            if (restarts > 0 && !r.Steps.Take(1).Any(s => s.Kind == "pause" && s.Seconds >= 31))
                errors.Add($"{r.Id}: gg_set_restart without the 31 s first");
            if (r.WordsFrom is var (wpath, wwords) && !WordsAtHead(wpath, wwords))
                errors.Add($"{r.Id}: the words {Py.Repr(wwords)} are no longer in the committed code under {wpath.Split('/')[0]}/: the stand-in or the row is out of date");
        }
        if (RealNameIn(Me))
            errors.Add("the test name is a real name");
        if (ContentName == "?" || Level <= 0)
            errors.Add("bridge/Shared/HudVersion.cs's Contents cannot be read: the rows fake the versions around its last line");
        if (!buttons.Any(b => b.StartsWith("gg_hudver_", StringComparison.Ordinal)))
            errors.Add("no version button (gg_hudver_...) in the committed layout");
        return new Info
        {
            Picks = picks, ByName = byName, Buttons = buttons, Named = named, Commit = HeadCommit(), Content = ContentName,
            Plugin = HeadVersion("plugin/OverStrike2/OverStrike2.cs", @"Version = ""([\d.]+)"""), Problems = errors,
        };
    }

    static void RequireValid(Info info)
    {
        if (info.Problems.Count > 0)
            throw Out.Stop("VALIDATION FAILED at HEAD " + info.Commit + ":\n  " + string.Join("\n  ", info.Problems));
    }

    static void WarnProblems(Info info)
    {
        if (info.Problems.Count == 0)
            return;
        Out.Print($"PROBLEMS at HEAD {info.Commit}: the committed code has moved past this table; --selfcheck and the real run refuse until the rows follow:");
        foreach (var e in info.Problems)
            Out.Print("  " + e);
    }

    // ---------------------------------------------------------------- the plan

    static (List<Entry> Plan, List<string> Notes) BuildPlan(IReadOnlyList<string>? only, bool bridgeCopy)
    {
        var byId = new Dictionary<string, Row>();
        foreach (var r in Rows)
            byId[r.Id] = r;
        var notes = new List<string>();
        HashSet<string>? wanted = null;
        if (only is not null)
        {
            wanted = [];
            foreach (var rid in only)
            {
                if (!byId.TryGetValue(rid, out var row))
                    throw Out.Stop($"--only: no row {rid}");
                if (row.Status != Ready)
                    notes.Add($"NOT RUN {rid} ({row.Status}): {row.Reason}");
                else
                    wanted.Add(rid);
            }
        }
        var needed = new HashSet<string>();
        if (wanted is not null)
            foreach (var rid in wanted)
                for (var a = byId[rid].After; a.Length > 0; a = byId[a].After)
                    needed.Add(a);
        var plan = new List<Entry>();
        var gone = new HashSet<string>();
        int? previousIndex = null;
        for (var index = 0; index < Rows.Count; index++)
        {
            var r = Rows[index];
            var selected = wanted is null || wanted.Contains(r.Id);
            if (!(selected || needed.Contains(r.Id)) || r.Status != Ready)
                continue;
            if (r.Bridge.Length > 0 && !bridgeCopy)
            {
                notes.Add($"SKIPPED {r.Id}: it writes the bridge's data ({r.Bridge}); only with --bridge-copy");
                gone.Add(r.Id);
                continue;
            }
            if (r.Renamed && !Renamed)
            {
                notes.Add($"SKIPPED {r.Id}: its own words name the client, where no mask can go; only with --renamed");
                gone.Add(r.Id);
                continue;
            }
            if (r.After.Length > 0 && gone.Contains(r.After))
            {
                notes.Add($"SKIPPED {r.Id}: after {r.After}, skipped");
                gone.Add(r.Id);
                continue;
            }
            var jump = (previousIndex is not null && previousIndex != index - 1) || previousIndex is null;
            plan.Add(new Entry(r, selected ? "shoot" : "setup", jump && r.After.Length == 0));
            previousIndex = index;
        }
        return (plan, notes);
    }

    /// <summary>The frames a row writes, in order.</summary>
    static List<string> FrameNames(Row row)
    {
        var total = row.Steps.Where(s => s.Kind == "shoot").Sum(s => s.N);
        return total == 1 ? [row.Id] : Enumerable.Range(0, total).Select(i => $"{row.Id}-{i + 1:00}").ToList();
    }

    // the seams the self-check replaces: the harness's verbs and bridgepost, the sleeps, the lock, A2S, the candidate's check
    static Func<string[], List<string>> HarnessArgv = RealHarnessArgv;
    static Func<string, List<string>> PostArgv = RealPostArgv;
    static Action<double> Sleep = s => Thread.Sleep(TimeSpan.FromSeconds(Math.Max(0, s)));
    static Func<string?> ReadLock = () => ReadLockFile();
    static Func<A2sAnswer?> AskA2s = () => A2s.Ask(Host, Port, 3.0);
    static Func<(bool Ok, string Words)> CandidateUp = RealCandidateUp;

    static List<string> RealHarnessArgv(string[] verbArgs)
    {
        if (!AllVerbs.Contains(verbArgs[0]))
            throw new InvalidOperationException($"verb {verbArgs[0]}: this drives only {string.Join(", ", AllVerbs)}");
        return ["pwsh", "-NoProfile", "-File", HarnessPath, .. verbArgs];
    }

    static List<string> RealPostArgv(string text) => [.. Proc.Self(), "bridgepost", .. ConfigPath is not null ? (string[])["--config", ConfigPath] : [], .. Words(text)];

    /// <summary>Full size, always: -MaxWidth 0 (the harness's default of 1920 stored the 2560 and 3840 shots 1920 wide, and the owner reviews on a 4K screen).</summary>
    static List<string> ShotArgv(string name, string raw) => HarnessArgv(["shot", "-Name", name, "-MaxWidth", "0", "-OutDir", raw]);

    static List<string> RconArgv(string cmd) => HarnessArgv(["rcon", "-Rcon", cmd]);

    static List<string> ConArgv(string cmd, int waitMs) => HarnessArgv(["cmd", "-Cmd", cmd, "-WaitMs", waitMs.ToString(CultureInfo.InvariantCulture)]);

    static string StartCommand(string res) =>
        $"pwsh -NoProfile -File {HarnessPath} start -Connect default -HudLocal -IncludeThumbs -AllowServerLock -Console -Resolution {res} -MaxMinutes 120 -Purpose \"docshots {res}\"";

    static string[] FinalCommands => ["css_gghud release", "css_gghud level 0", $"css_gghud content {Account} {Level}", $"gg_fun reset \"{Me}\""];

    // ---------------------------------------------------------------- the index

    const string Style = "<style>body{font:14px system-ui;background:#111;color:#ddd;margin:24px}h2{color:#E9B44C;margin-top:32px}figure{display:inline-block;margin:8px;vertical-align:top;width:620px}"
                         + "img{width:620px;border:1px solid #333}figcaption{margin-top:4px}table{border-collapse:collapse}th,td{text-align:left;vertical-align:top;padding:3px 12px 3px 0}th{color:#E9B44C;white-space:nowrap}"
                         + "small{color:#99a}</style>";

    static string Caption(Row row, Dictionary<string, Pick> byName, string res)
    {
        var words = $"<b>{Py.HtmlEscape(row.Id)}</b> at {Py.HtmlEscape(res)}: {Py.HtmlEscape(row.Shows)}";
        if (row.Pick.Length > 0)
            words += $"<br><small>Message: {Py.HtmlEscape(row.Pick)}. Today: {Py.HtmlEscape(row.Today)}. Picked: {Py.HtmlEscape(byName[row.Pick].Surfaces)}</small>";
        if (row.StandIn)
            words += "<br><small>STAND-IN: the message's real words on its panel, sent with gg_notice; its real trigger would leave something behind.</small>";
        if (row.Names != NoName)
            words += $"<br><small>Names: {Py.HtmlEscape(row.Names)}</small>";
        return words;
    }

    /// <summary>index.html over every resolution in the folder: relative links, nothing from outside.</summary>
    static void WriteIndex(string output, Info info)
    {
        var manifests = new List<(string Res, JsonObject Man)>();
        if (Directory.Exists(output))
            foreach (var dir in Directory.GetDirectories(output).OrderBy(d => d, StringComparer.Ordinal))
            {
                var m = Path.Combine(dir, "manifest.json");
                if (File.Exists(m))
                    manifests.Add((Path.GetFileName(dir), (JsonObject)Json.Parse(File.ReadAllText(m, Encoding.UTF8))!));
            }
        var byName = info.ByName;
        var head = string.Join("; ", manifests.Select(x => $"{x.Res}: commit {Py.Str(Json.At(x.Man, "commit"))}, content {Py.Str(Json.At(x.Man, "content"))}, plugin {Py.Str(Json.At(x.Man, "plugin"))}, {Py.Str(Json.At(x.Man, "taken"))}"));
        if (head.Length == 0)
            head = "no run yet";
        var parts = new List<string> { "<!doctype html><meta charset=utf-8><title>Over Strike 2 - the whole HUD, documentation screenshots</title>", Style,
            $"<h1>Over Strike 2 - the whole HUD: real-client screenshots, insecure test copy, loose HUD files ({Py.HtmlEscape(head)})</h1>",
            "<p>Taken by docshots.py. Test names only: the avatar and chat are blurred on every frame, and the client's own name is painted over where a row says so. "
            + "Review every picture before it goes into the public docs.</p>" };
        var order = new List<string>(Sections);
        foreach (var p in info.Picks)
        {
            var sec = "Messages: " + p.Heading[0] + p.Heading[1..].ToLowerInvariant();
            if (!order.Contains(sec))
                order.Add(sec);
        }
        var shotRows = new Dictionary<string, List<Row>>();
        foreach (var r in Rows)
        {
            var sec = SectionOf(r, byName);
            if (!shotRows.TryGetValue(sec, out var list))
                shotRows[sec] = list = [];
            list.Add(r);
        }
        var failed = new List<(string Id, string Res, string Why)>();
        foreach (var sec in order)
        {
            var figures = new List<string>();
            foreach (var r in shotRows.GetValueOrDefault(sec) ?? [])
                foreach (var (res, man) in manifests)
                {
                    if (Json.At(man, "rows", r.Id) is not JsonObject got || got.Count == 0)
                        continue;
                    if (Json.Str(got, "failed") is { Length: > 0 } why)
                        failed.Add((r.Id, res, why));
                    foreach (var f in (Json.At(got, "frames") as JsonArray ?? []).OfType<JsonObject>())
                        foreach (var name in new[] { Json.Str(f, "file"), Json.Str(f, "crop") }.Where(n => !string.IsNullOrEmpty(n)))
                        {
                            var rel = $"{res}/{name}";
                            figures.Add($"<figure><a href=\"{Py.HtmlEscape(rel)}\"><img src=\"{Py.HtmlEscape(rel)}\"></a><figcaption>{Caption(r, byName, res)}</figcaption></figure>");
                        }
                }
            if (figures.Count > 0)
            {
                parts.Add($"<h2>{Py.HtmlEscape(sec)}</h2>");
                parts.AddRange(figures);
            }
        }
        parts.Add("<h2>What is not here, and why</h2><table>");
        foreach (var r in Rows.Where(r => r.Status != Ready))
            parts.Add($"<tr><th>{Py.HtmlEscape(r.Id)}</th><td>{Py.HtmlEscape(r.Status)}{(r.Pick.Length > 0 ? " - " + Py.HtmlEscape(r.Pick) : "")}: {Py.HtmlEscape(r.Reason)}</td></tr>");
        foreach (var (rid, res, why) in failed)
            parts.Add($"<tr><th>{Py.HtmlEscape(rid)}</th><td>FAILED at {Py.HtmlEscape(res)}: {Py.HtmlEscape(why)}</td></tr>");
        parts.Add("</table>");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "index.html"), string.Join(Environment.NewLine, parts), new UTF8Encoding(false));   // write_text: the system's line ends
    }

    // ---------------------------------------------------------------- the two runners: printing and doing

    /// <summary>One runner of a plan: the dry run's printer, or the real thing; walk goes through both alike.</summary>
    abstract class Runner
    {
        public abstract void Say(string text = "");
        public abstract void Preflight();
        public abstract void Begin(Entry e);
        public abstract void Rcon(Step s);
        public abstract void RconBg(Step s);
        public abstract void Con(Step s);
        public abstract void Post(Step s);
        public abstract void Pause(double seconds);
        public abstract void Mark();
        public abstract void WaitLog(Step s);
        public abstract void VoteGap();
        public abstract void VoteClosed();
        public abstract void VoteOpened();
        public abstract void Pressed();
        public abstract void Until(Step s);
        public abstract void Guard(Step s);
        public abstract void Shoot(Step s, List<string> names, Row row);
        public abstract void SetupShoot(Step s);
        public virtual void Fail(Entry e, string why)
        {
        }

        public virtual void FinishRow(Entry e)
        {
        }

        public abstract void Final();
    }

    /// <summary>What a run would execute, line for line, touching nothing: the same walk as the Doer's.</summary>
    class Printer(string res, string output, TextWriter? stream = null) : Runner
    {
        protected readonly string Res = res;
        protected readonly int W = ParseRes(res).W, H = ParseRes(res).H;
        protected readonly string Raw = Path.Combine(output, res, "_raw");
        readonly TextWriter stream = stream ?? Console.Out;
        public double Clock;
        double busy, closedAt;
        public int Frames;
        readonly Dictionary<string, double?> at = new() { ["join"] = null, ["vote"] = null, ["closed"] = null, ["press"] = null };   // until's anchors (Anchors)

        public override void Say(string text = "") => stream.WriteLine(text);

        public virtual string Cmdline(List<string> argv) => Py.List2CmdLine(argv);

        public override void Preflight()
        {
            Say($"GATE (the run refuses otherwise): {LockPath ?? "None"} exists and starts with '{OwnerLine}'; A2S on {Host}:{Port} says VAC off, at most 1 human, map {StartMap}");
            if (Renamed)
            {
                Say($"  $ {Cmdline(RconArgv("ggd_name"))}    --renamed: must say the client goes by {Me}, and is");
                Clock += TRcon;
            }
            Say($"PREFLIGHT: the client started by hand: {StartCommand(Res)}");
            Say($"  $ {Cmdline(ConArgv("status", 2000))}    must name {Host}:{Port} and insecure");
            Say($"  $ {Cmdline(ShotArgv("_probe", Raw))}    the frame must be {Res}, not scaled");
            Say($"  $ {Cmdline(RconArgv("css_gghud"))}    must have an entity, the bridge ok, no vote on");
            Say($"  $ {Cmdline(ConArgv("jointeam 2 1", 1500))}");
            Say($"  $ {Cmdline(RconArgv($"css_gghud content {Account} {Level}"))}    which HUD this game holds, so the window's X is the plain close (a real join forgets it: the first row)");
            Say($"  $ {Cmdline(RconArgv("mp_restartgame 1"))}    a fresh match: an Arms Race match ends after 60 minutes, and its end reloads the map under the run");
            Clock += TCon + TShot + TRcon + TCon + TRcon + TRcon;
        }

        public override void Begin(Entry e) => Say($"== {e.Row.Id}{(e.Mode == "setup" ? " (setup only, no picture)" : "")}: {e.Row.Shows}");

        public override void Rcon(Step s)
        {
            Clock = Math.Max(Clock, busy) + TRcon;
            Say($"  $ {Cmdline(RconArgv(s.Text))}" + (s.Expect.Length > 0 ? $"    reply must match /{s.Expect}/" : ""));
        }

        public override void RconBg(Step s)
        {
            Clock = Math.Max(Clock, busy);
            busy = Clock + TRcon;
            Say($"  (background, waited for before the next RCON call) $ {Cmdline(RconArgv(s.Text))}" + (s.Expect.Length > 0 ? $"    reply must match /{s.Expect}/" : ""));
        }

        public override void Con(Step s)
        {
            Clock += TCon + s.WaitMs / 1000.0;
            if (s.Text.StartsWith("jointeam", StringComparison.Ordinal))
                at["join"] = Clock;
            Say($"  $ {Cmdline(ConArgv(s.Text, s.WaitMs))}");
        }

        public override void Post(Step s)
        {
            Clock += TPost;
            Say($"  $ {Cmdline(PostArgv(s.Text))}    a candidate bridge on a test copy or it refuses; a refusal fails the row");
        }

        public override void Until(Step s)
        {
            var when = at[s.Text];
            if (when is not null)
                Clock = Math.Max(Clock, when.Value + s.Seconds);
            Say($"  sleep until {Py.G(s.Seconds)} s after {Anchors[s.Text]}" + (when is not null ? "" : " (not yet in this plan: the row fails)"));
        }

        public override void VoteOpened()
        {
            at["vote"] = Clock;
            Say("  (the vote has opened: its clocks count from here)");
        }

        public override void Pressed()
        {
            at["press"] = Clock;
            Say("  (the press is marked: what it starts counts from here)");
        }

        public override void Pause(double seconds)
        {
            Clock += seconds;
            Say($"  sleep {Py.G(seconds)}");
        }

        public override void Mark()
        {
            Clock += TLog;
            Say($"  $ {Cmdline(HarnessArgv(["log", "-Tail", "1"]))}    its 'lines: N' is the mark");
        }

        public override void WaitLog(Step s)
        {
            Clock += TWaitLog;
            Say($"  every 3 s, up to {Py.G(s.Seconds)} s: $ {Cmdline(HarnessArgv(["log", "-Tail", "1", "-Pattern", s.Text, "-FromLine", "<mark>"]))}");
        }

        public override void VoteGap()
        {
            var wait = Math.Max(0.0, closedAt + DocShots.VoteGap - Clock);
            Clock += wait;
            Say($"  sleep until {DocShots.VoteGap} s after the last vote closed (MapVote.WaitSeconds, about {Py.F0(wait)} s here)");
        }

        public override void VoteClosed()
        {
            closedAt = Clock;
            at["closed"] = Clock;
            Say("  (the vote has closed: the wait starts)");
        }

        public override void Guard(Step s) => Say($"  refuse the row unless the last vote closed less than {Py.G(s.Seconds)} s ago");

        public override void Shoot(Step s, List<string> names, Row row)
        {
            for (var i = 0; i < names.Count; i++)
            {
                Say($"  {(s.Parallel ? "(side by side) " : "")}$ {Cmdline(ShotArgv(names[i], Raw))}");
                Say($"    then: {DescribePost(row, W, H)}");
                Frames++;
                if (s.Parallel)
                    Clock += s.Gap;
                else
                {
                    Clock += TShot;
                    if (s.Gap != 0 && i < names.Count - 1)
                        Clock += s.Gap;
                }
            }
            if (s.Parallel)
                Clock += TShot;
        }

        public override void SetupShoot(Step s) => Pause(!s.Parallel ? s.N * TShot : s.N * s.Gap + TShot);

        public override void Final()
        {
            Say("== at the end, whatever happened: back to neutral");
            foreach (var cmd in FinalCommands)
            {
                Say($"  $ {Cmdline(RconArgv(cmd))}");
                Clock += TRcon;
            }
            Say($"  $ {Cmdline(ConArgv("-showscores", 500))}");
        }
    }

    static readonly Regex[] Secretish =
    [
        new(@"(/log/|/hud/|/act/|/health/|/dash/|/search/|/catalog/|/restart-notice/|/updated/)[0-9A-Za-z_-]{8,}"),
        new(@"(?i)\b(rcon_password|netconpassword|password|pass)\b\s*[=: ]\s*\S+"),
        new(@"\b[0-9a-fA-F]{24,}\b"),
    ];

    /// <summary>No secret, name or address leaves through what this prints or logs.</summary>
    static string Scrub(string text)
    {
        foreach (var rx in Secretish)
            text = rx.Replace(text, m => (m.Groups.Count > 1 && m.Groups[1].Success ? m.Groups[1].Value : "") + "<hidden>");
        foreach (var n in RealNames)
            text = Regex.Replace(text, Regex.Escape(n), "<player>", RegexOptions.IgnoreCase);
        return text.Replace(Account, "<account>", StringComparison.Ordinal).Replace(Host, "<server>", StringComparison.Ordinal);
    }

    /// <summary>The text without this PC's own folder names (the repository, the lock file's, this program's, the home folder), which may hold a user name.</summary>
    static string LocalPathsOut(string text)
    {
        var self = Proc.Self();
        var paths = new HashSet<string> { Os2, Harness.MainCheckout(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var p in self)
            if (Path.IsPathRooted(p))
            {
                paths.Add(p);
                paths.Add(Path.GetDirectoryName(p) ?? p);
            }
        if (LockPath is not null)
        {
            paths.Add(LockPath);
            paths.Add(Path.GetDirectoryName(LockPath) ?? LockPath);
        }
        foreach (var path in paths.Where(p => p.Length > 0).OrderByDescending(p => p.Length))
            text = text.Replace(path, "", StringComparison.Ordinal);
        return text;
    }

    /// <summary>The real thing: each verb a process of its own, its output in a file, not a pipe (a child that inherits a pipe can keep the call open
    /// past its timeout).</summary>
    sealed class Doer : Runner, IDisposable
    {
        readonly string res, output, resDir, raw, tmp;
        readonly int w, h;
        readonly bool keepRaw;
        readonly StreamWriter logf;
        int count;
        (Spawned P, string Path, string Label, string Expect)? bg;
        int markLine;
        double closedAt = Now();      // a run may follow another's vote: the first vote gap waits it out
        readonly Dictionary<string, double?> at = new() { ["join"] = null, ["vote"] = null, ["closed"] = null, ["press"] = null };   // until's anchors (Anchors), as this run met them
        bool voteSeen;
        public readonly Dictionary<string, List<string>> FramesShot = [];
        public readonly Dictionary<string, string> Failed = [];

        public Doer(string res, string output, bool keepRaw)
        {
            this.res = res;
            (w, h) = ParseRes(res);
            this.output = output;
            resDir = Path.Combine(output, res);
            raw = Path.Combine(resDir, "_raw");
            tmp = Path.Combine(resDir, "_tmp");
            this.keepRaw = keepRaw;
            Directory.CreateDirectory(raw);
            Directory.CreateDirectory(tmp);
            logf = new StreamWriter(Path.Combine(resDir, "run.log"), append: true, new UTF8Encoding(false));
        }

        static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public void Dispose() => logf.Dispose();

        public override void Say(string text = "")
        {
            Console.Out.WriteLine(Scrub(text));
            Console.Out.Flush();
            Log(text);
        }

        void Log(string text)
        {
            logf.WriteLine($"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} {Scrub(text)}");
            logf.Flush();
        }

        (Spawned, string) Launch(List<string> argv)
        {
            count++;
            var path = Path.Combine(tmp, $"{count:00000}.txt");
            return (Spawned.Start(argv, path), path);
        }

        string Collect(Spawned p, string path, double timeout, string label)
        {
            using (p)
            {
                if (!p.Wait(timeout, out var code))
                {
                    p.KillTree();
                    throw new RowFailed($"{label}: no answer in {Py.G(timeout)} s");
                }
                var text = Encoding.UTF8.GetString(File.ReadAllBytes(path)).Replace("\0", "", StringComparison.Ordinal);
                File.Delete(path);
                Log($"{label} -> exit {code}: {Py.Slice(Py.Strip(text), 0, 600)}");
                if (code != 0 || text.TrimStart().StartsWith("ERROR:", StringComparison.Ordinal))
                    throw new RowFailed($"{label}: exit {code}: {Py.Slice(Scrub(Py.Strip(text)), 0, 300)}");
                return text;
            }
        }

        string RunVerb(List<string> argv, double timeout, string label)
        {
            var (p, path) = Launch(argv);
            return Collect(p, path, timeout, label);
        }

        void JoinBg()
        {
            if (bg is not var (p, path, label, expect))
                return;
            bg = null;
            var text = Collect(p, path, 60, label);
            if (expect.Length > 0 && !Regex.IsMatch(text, expect))
                throw new RowFailed($"{label}: the reply does not say /{expect}/: {Py.Slice(Scrub(Py.Strip(text)), 0, 300)}");
        }

        public override void Rcon(Step s)
        {
            JoinBg();
            var text = RunVerb(RconArgv(s.Text), 60, $"rcon {s.Text}");
            if (s.Expect.Length > 0 && !Regex.IsMatch(text, s.Expect))
                throw new RowFailed($"rcon {s.Text}: the reply does not say /{s.Expect}/: {Py.Slice(Scrub(Py.Strip(text)), 0, 300)}");
        }

        public override void RconBg(Step s)
        {
            JoinBg();
            var (p, path) = Launch(RconArgv(s.Text));
            bg = (p, path, $"rcon {s.Text}", s.Expect);
        }

        public override void Con(Step s)
        {
            RunVerb(ConArgv(s.Text, s.WaitMs), 30, $"cmd {s.Text}");
            if (s.Text.StartsWith("jointeam", StringComparison.Ordinal))
                at["join"] = Now();
        }

        public override void Until(Step s)
        {
            var when = at[s.Text] ?? throw new RowFailed($"until: {Anchors[s.Text]} has not happened in this run");
            var wait = when + s.Seconds - Now();
            if (wait > 0)
                Sleep(wait);
        }

        public override void VoteOpened() => at["vote"] = Now();

        public override void Pressed() => at["press"] = Now();

        public override void Post(Step s) => RunVerb(PostArgv(s.Text), 60, $"post {s.Text}");

        public override void Pause(double seconds) => Sleep(seconds);

        public override void Mark()
        {
            var text = RunVerb(HarnessArgv(["log", "-Tail", "1"]), 30, "log");
            var m = Regex.Match(text, @"lines: (\d+)");
            markLine = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        public override void WaitLog(Step s)
        {
            var end = Now() + s.Seconds;
            while (Now() < end)
            {
                var text = RunVerb(HarnessArgv(["log", "-Tail", "1", "-Pattern", s.Text, "-FromLine", markLine.ToString(CultureInfo.InvariantCulture)]), 30, "log");
                if (Regex.IsMatch(text, @"^\s*\d+: ", RegexOptions.Multiline))
                    return;
                Sleep(3);
            }
            throw new RowFailed($"no /{s.Text}/ in the client's log in {Py.G(s.Seconds)} s");
        }

        public override void VoteGap()
        {
            JoinBg();
            var wait = closedAt + DocShots.VoteGap - Now();
            if (wait > 0)
            {
                Say($"  waiting {Py.F0(wait)} s: the wait after a vote");
                Sleep(wait);
            }
        }

        public override void VoteClosed()
        {
            closedAt = Now();
            at["closed"] = closedAt;
            voteSeen = true;
        }

        public override void Guard(Step s)
        {
            if (!voteSeen || Now() - closedAt >= s.Seconds)
                throw new RowFailed($"the guard: no vote closed in the last {Py.G(s.Seconds)} s, so this click could open one");
        }

        bool CheckFrame(string text, string name)
        {
            var m = Regex.Match(text, @"ok (\d+)x(\d+) \(from (\d+)x(\d+)\)");
            if (!m.Success)
                throw new RowFailed($"shot {name}: no size in its answer: {Py.Slice(Scrub(Py.Strip(text)), 0, 200)}");
            var size = $"{m.Groups[1].Value}x{m.Groups[2].Value}";
            if (size != res)
                throw new RowFailed($"shot {name}: stored at {size}, the run is at {res}: nothing may be scaled");
            return text.Contains("WARNING", StringComparison.Ordinal);
        }

        public override void Shoot(Step s, List<string> names, Row row)
        {
            if (!FramesShot.TryGetValue(row.Id, out var got))
                FramesShot[row.Id] = got = [];
            if (s.Parallel)
            {
                var procs = new List<(string Name, Spawned P, string Path)>();
                foreach (var name in names)
                {
                    var (p, path) = Launch(ShotArgv(name, raw));
                    procs.Add((name, p, path));
                    Sleep(s.Gap);
                }
                var lost = 0;
                foreach (var (name, p, path) in procs)
                {
                    try
                    {
                        CheckFrame(Collect(p, path, 60, $"shot {name}"), name);
                        got.Add(name);
                    }
                    catch (RowFailed e)
                    {
                        lost++;
                        Log($"a frame lost: {e.Message}");
                    }
                }
                if (lost == names.Count)
                    throw new RowFailed("every side-by-side frame was lost");
                return;
            }
            for (var i = 0; i < names.Count; i++)
            {
                var text = RunVerb(ShotArgv(names[i], raw), 60, $"shot {names[i]}");
                if (CheckFrame(text, names[i]))          // a black frame: a map loading
                {
                    Sleep(3);
                    CheckFrame(RunVerb(ShotArgv(names[i], raw), 60, $"shot {names[i]} again"), names[i]);
                }
                got.Add(names[i]);
                if (s.Gap != 0 && i < names.Count - 1)
                    Sleep(s.Gap);
            }
        }

        public override void SetupShoot(Step s) => Sleep(!s.Parallel ? s.N * TShot : s.N * s.Gap + TShot);

        public override void Begin(Entry e) => Say($"== {e.Row.Id}{(e.Mode == "setup" ? " (setup only)" : "")}");

        public override void Fail(Entry e, string why)
        {
            Failed[e.Row.Id] = why;
            Say($"   FAILED {e.Row.Id}: {why}");
        }

        public override void Preflight()
        {
            var text = RunVerb(ConArgv("status", 2000), 30, "cmd status");
            if (!text.Contains($"{Host}:{Port}", StringComparison.Ordinal) || !text.Contains("insecure", StringComparison.Ordinal))
                throw Out.Stop("REFUSED: the client is not an insecure session in game on the test copy (cmd status). Start it by hand:\n  " + StartCommand(res));
            text = RunVerb(ShotArgv("_probe", raw), 60, "shot _probe");
            var m = Regex.Match(text, @"ok (\d+)x(\d+) \(from (\d+)x(\d+)\)");
            if (!m.Success || $"{m.Groups[1].Value}x{m.Groups[2].Value}" != res || $"{m.Groups[3].Value}x{m.Groups[4].Value}" != res)
                throw Out.Stop($"REFUSED: the client's frame is not {res}: {Py.Slice(Scrub(Py.Strip(text)), 0, 200)}\nStart it at that size:\n  " + StartCommand(res));
            if (Renamed)
            {
                text = RunVerb(RconArgv("ggd_name"), 60, "rcon ggd_name");
                if (!text.Contains($"{Account} goes by {Me}", StringComparison.Ordinal) || !text.Contains($"{Account} is {Me}", StringComparison.Ordinal))
                    throw Out.Stop($"REFUSED: --renamed, and the server does not call the client {Me} (GgDocName on the test copy, ggd_name {Account} {Me}): {Py.Slice(Scrub(Py.Strip(text)), 0, 200)}");
            }
            text = RunVerb(RconArgv("css_gghud"), 60, "rcon css_gghud");
            if (!Regex.IsMatch(text, @"entity \d+") || !text.Contains("bridge ok", StringComparison.Ordinal))
                throw Out.Stop($"REFUSED: the HUD is not up on the server: {Py.Slice(Scrub(Py.Strip(text)), 0, 400)}");
            if (Regex.IsMatch(text, @"vote round \d+ open"))
                throw Out.Stop("REFUSED: a vote is open");
            RunVerb(ConArgv("jointeam 2 1", 1500), 30, "cmd jointeam");
            RunVerb(RconArgv($"css_gghud content {Account} {Level}"), 60, "rcon css_gghud content");
            RunVerb(RconArgv("mp_restartgame 1"), 60, "rcon mp_restartgame 1");
        }

        public override void Final()
        {
            Say("== back to neutral");
            try
            {
                JoinBg();
            }
            catch (RowFailed e)
            {
                Log($"final: {e.Message}");
            }
            foreach (var cmd in FinalCommands)
            {
                try
                {
                    RunVerb(RconArgv(cmd), 60, $"rcon {cmd}");
                }
                catch (RowFailed e)
                {
                    Say($"   the end's {cmd}: {e.Message}");
                }
            }
            try
            {
                RunVerb(ConArgv("-showscores", 500), 30, "cmd -showscores");
            }
            catch (RowFailed e)
            {
                Log($"final: {e.Message}");
            }
        }

        public void WritePictures(List<Entry> plan, Info info)
        {
            var rows = new JsonObject();
            var byId = new Dictionary<string, Row>();
            foreach (var e in plan)
                byId[e.Row.Id] = e.Row;
            foreach (var (rid, names) in FramesShot)
            {
                var row = byId[rid];
                var frames = new JsonArray();
                foreach (var name in names)
                {
                    var rawFile = Path.Combine(raw, $"{name}.png");
                    if (!File.Exists(rawFile))
                        continue;
                    var (file, crop) = PostFrame(rawFile, row, resDir);
                    frames.Add(new JsonObject { ["file"] = file, ["crop"] = crop });
                }
                rows[rid] = new JsonObject { ["frames"] = frames };
            }
            foreach (var (rid, why) in Failed)
            {
                if (rows[rid] is not JsonObject r)
                    rows[rid] = r = new JsonObject { ["frames"] = new JsonArray() };
                r["failed"] = why;
            }
            var manifest = new JsonObject
            {
                ["res"] = res, ["commit"] = info.Commit, ["content"] = info.Content, ["plugin"] = info.Plugin, ["taken"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                ["renamed"] = Renamed, ["built"] = Built, ["rows"] = rows,
            };
            File.WriteAllText(Path.Combine(resDir, "manifest.json"), Json.Dumps(manifest, indent: 1).Replace("\n", Environment.NewLine, StringComparison.Ordinal), new UTF8Encoding(false));
            WriteIndex(output, info);
            if (!keepRaw && Directory.Exists(raw))
                Directory.Delete(raw, true);
            if (Directory.Exists(tmp))
                Directory.Delete(tmp, true);
            logf.Dispose();
        }
    }

    static void RunStep(Runner runner, Step s, Row row, string mode, List<string> names)
    {
        switch (s.Kind)
        {
            case "rcon":
                runner.Rcon(s);
                break;
            case "rcon_bg":
                runner.RconBg(s);
                break;
            case "con":
                runner.Con(s);
                break;
            case "post":
                runner.Post(s);
                break;
            case "pause":
                runner.Pause(s.Seconds);
                break;
            case "mark":
                runner.Mark();
                break;
            case "wait_log":
                runner.WaitLog(s);
                break;
            case "vote_gap":
                runner.VoteGap();
                break;
            case "vote_closed":
                runner.VoteClosed();
                break;
            case "vote_opened":
                runner.VoteOpened();
                break;
            case "pressed":
                runner.Pressed();
                break;
            case "until":
                runner.Until(s);
                break;
            case "guard":
                runner.Guard(s);
                break;
            case "shoot":
                if (mode == "setup")
                    runner.SetupShoot(s);
                else
                {
                    var these = names.Take(s.N).ToList();
                    names.RemoveRange(0, these.Count);
                    runner.Shoot(s, these, row);
                }
                break;
            default:
                throw new InvalidOperationException(s.Kind);
        }
    }

    /// <summary>The one walk through a plan: the printer and the real run go through the same code, so the dry run prints what the run does.</summary>
    static HashSet<string> Walk(Runner runner, List<Entry> plan)
    {
        runner.Preflight();
        var failed = new HashSet<string>();
        try
        {
            foreach (var e in plan)
            {
                var r = e.Row;
                if (r.After.Length > 0 && failed.Contains(r.After))
                {
                    runner.Say($"== {r.Id}: skipped: {r.After} failed");
                    failed.Add(r.Id);
                    continue;
                }
                runner.Begin(e);
                var names = FrameNames(r);
                try
                {
                    if (e.Jump && r.Win == Win.None)
                        RunStep(runner, Release, r, e.Mode, names);
                    foreach (var s in r.Steps)
                        RunStep(runner, s, r, e.Mode, names);
                }
                catch (RowFailed why)
                {
                    runner.Fail(e, why.Message);
                    failed.Add(r.Id);
                }
                foreach (var s in r.Cleanup)
                {
                    try
                    {
                        RunStep(runner, s, r, e.Mode, names);
                    }
                    catch (RowFailed why)
                    {
                        runner.Say($"   {r.Id}: its cleanup: {why.Message}");
                    }
                }
                runner.FinishRow(e);
            }
        }
        finally
        {
            runner.Final();
        }
        return failed;
    }

    // ---------------------------------------------------------------- the gate

    /// <summary>(true, what it found) or (false, why not). The lock first: no packet goes out without it.</summary>
    static (bool Ok, string Words) Gate(Func<string?> readLock, Func<A2sAnswer?> askA2s)
    {
        if (SteamAccountText.Length == 0)
            return (false, "REFUSED: Steam names no logged-in account on this PC: the rows would address a made-up client");
        var text = readLock();
        if (text is null)
            return (false, "REFUSED: the server lock does not exist: it is not this harness's test (probe lock)");
        if (!text.StartsWith(OwnerLine, StringComparison.Ordinal))
            return (false, $"REFUSED: the server lock is not the harness's: its first line is: {(text.Length > 0 ? Py.Slice(Py.SplitLines(text)[0], 0, 120) : "(empty)")}");
        var info = askA2s();
        if (info is null)
            return (false, "REFUSED: A2S does not answer: the test copy cannot be proven to be VAC off");
        if (info.Vac != 0)
            return (false, $"REFUSED: A2S says VAC on (server {Py.Repr(info.Name)}): that is production, not the test copy");
        if (info.Humans > 1)
            return (false, $"REFUSED: {info.Humans} humans on the test copy: a real player's name could reach a picture");
        if (info.Map != StartMap)
            return (false, $"REFUSED: the map is {info.Map}, the rows expect {StartMap} (official, so no download dialog): rcon changelevel {StartMap} first");
        return (true, $"A2S: {Py.Repr(info.Name)}, {info.Map}, {info.Humans} human(s), VAC off");
    }

    static string? ReadLockFile(string? path = null)
    {
        path ??= LockPath;
        if (path is null)
            return null;     // no private config, or no server.lock_file in it: no lock can be this harness's
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- the commands

    static void CmdList(Info info)
    {
        Out.Print($"HEAD {info.Commit}: content {info.Content}, plugin {info.Plugin}. The message list: {Path.GetFileName(PicksPath)}");
        Out.Print($"Layout checked at HEAD: {Layout}, {info.Buttons.Count} buttons; the table names {info.Named.Count}, all there.");
        var byName = info.ByName;
        foreach (var r in Rows)
        {
            Out.Print($"{r.Id}  [{r.Status}]{(r.Bridge.Length > 0 ? "  [needs --bridge-copy]" : "")}{(r.StandIn ? "  [stand-in]" : "")}{(r.After.Length > 0 ? "  after " + r.After : "")}");
            Out.Print($"    shows: {r.Shows}");
            if (r.Pick.Length > 0)
            {
                Out.Print($"    pick: {r.Pick} [{byName[r.Pick].Bracket}]");
                Out.Print($"    today: {r.Today}");
                Out.Print($"    picked: {byName[r.Pick].Surfaces}");
            }
            else if (r.Today.Length > 0)
                Out.Print($"    today: {r.Today}");
            if (r.Status == Ready)
            {
                Out.Print($"    names: {r.Names}");
                Out.Print($"    when: {r.When}");
                Out.Print("    how: " + string.Join("; ", r.Steps.Select(StepWords)));
                if (r.Cleanup.Count > 0)
                    Out.Print("    cleanup: " + string.Join("; ", r.Cleanup.Select(StepWords)));
                if (r.Crop is { } c)
                    Out.Print($"    crop at 1080p: {Py.G(c.X0)},{Py.G(c.Y0)} - {Py.G(c.X1)},{Py.G(c.Y1)} ({c.Anchor})");
                if (r.Bridge.Length > 0)
                    Out.Print($"    writes in the bridge's data: {r.Bridge}");
            }
            else
                Out.Print($"    why: {r.Reason}");
        }
        Out.Print("");
        PrintCounts(info);
    }

    static string StepWords(Step s) => s.Kind switch
    {
        "rcon" => $"rcon {s.Text}",
        "rcon_bg" => $"rcon in the background {s.Text}",
        "con" => $"cmd {s.Text}",
        "post" => $"bridgepost.py {s.Text}",
        "pause" => $"sleep {Py.G(s.Seconds)}",
        "shoot" => $"shot x{s.N}" + (s.Parallel ? " side by side" : ""),
        "mark" => "mark the client's log",
        "wait_log" => "wait for in game in the log",
        "vote_gap" => $"wait out the {VoteGap} s after a vote",
        "vote_closed" => "(the vote has closed)",
        "guard" => $"refuse unless a vote closed under {Py.G(s.Seconds)} s ago",
        "vote_opened" => "(the vote has opened)",
        "pressed" => "(the press is marked)",
        "until" => $"sleep until {Py.G(s.Seconds)} s after {Anchors[s.Text]}",
        _ => throw new InvalidOperationException(s.Kind),
    };

    static void PrintCounts(Info info)
    {
        var counts = Statuses.ToDictionary(st => st, st => Rows.Count(r => r.Status == st));
        Out.Print("Rows: " + string.Join(", ", Statuses.Select(st => $"{counts[st]} {st}")) + $"; {Rows.Count} in all.");
        var picks = info.Picks;
        var pickStatus = new Dictionary<string, string>();
        foreach (var p in picks)
        {
            var states = Rows.Where(r => r.Pick == p.Name).Select(r => r.Status).ToHashSet();
            pickStatus[p.Name] = states.Contains(Ready) ? Ready : states.Order(StringComparer.Ordinal).First();
        }
        Out.Print($"The {picks.Count} picks: " + string.Join(", ", Statuses.Select(st => $"{pickStatus.Values.Count(v => v == st)} {st}")) + "; every pick has a row.");
        var ready = Rows.Where(r => r.Status == Ready).ToList();
        Out.Print($"Of the {counts[Ready]} ready rows: {ready.Count(r => r.Bridge.Length > 0)} need --bridge-copy, {ready.Count(r => r.StandIn)} are stand-ins by gg_notice, "
                  + $"{ready.Count(r => r.Names.StartsWith("real name", StringComparison.Ordinal))} would show a real name and mask it, {ready.Count(r => r.Pick.Length > 0)} show a pick on today's surface: retake those when the message system lands.");
        Out.Print($"Every ready frame also gets the avatar and, but for {ready.Count(r => r.Chat)} chat rows, the chat zone blurred.");
    }

    static void CmdDryRun(IEnumerable<string> resList, IReadOnlyList<string>? only, bool bridgeCopy, string output, TextWriter? stream = null)
    {
        stream ??= Console.Out;
        foreach (var res in resList)
        {
            if (!Resolutions.Contains(res))
                throw Out.Stop($"--res {res}: not one of the owner's: {string.Join(", ", Resolutions)}");
            var (plan, notes) = BuildPlan(only, bridgeCopy);
            stream.WriteLine($"######## {res}: what a run would execute, nothing is touched (out: {output})");
            if (bridgeCopy)
                stream.WriteLine($"GATE with --bridge-copy (the run refuses otherwise): {Py.List2CmdLine(PostArgv("check"))}");
            foreach (var n in notes)
                stream.WriteLine(n);
            if (plan.Count == 0)
            {
                stream.WriteLine("Nothing to run.");
                continue;
            }
            var p = new Printer(res, output, stream);
            Walk(p, plan);
            stream.WriteLine($"-------- {res}: {plan.Count(e => e.Mode == "shoot")} rows, {p.Frames} frames at full size, about {Py.F0(p.Clock / 60)} min by the verbs' timings (T_RCON, T_SHOT, ...) "
                         + $"(-MaxMinutes in the start command: 120), {notes.Count} not run.");
        }
        stream.Flush();
    }

    /// <summary>--bridge-copy's word held to the facts: bridgepost's checks, nothing sent. (true, words) or (false, why). Its output in a file.</summary>
    static (bool, string) RealCandidateUp()
    {
        var file = Path.GetTempFileName();
        try
        {
            using var p = Spawned.Start(PostArgv("check"), file);
            if (!p.Wait(120, out var code))
            {
                p.KillTree();
                return (false, "bridgepost check: no answer in 120 s");
            }
            return (code == 0, Py.Strip(Encoding.UTF8.GetString(File.ReadAllBytes(file))));
        }
        finally
        {
            File.Delete(file);
        }
    }

    sealed record Options(string? Res, string? Out, IReadOnlyList<string>? Only, bool BridgeCopy, bool KeepRaw);

    static int CmdRun(Options args, Info info)
    {
        var (ok, words) = Gate(ReadLock, AskA2s);
        Out.Print(Scrub(words));
        if (!ok)
            return 2;
        if (args.BridgeCopy)
        {
            (ok, words) = CandidateUp();
            Out.Print(Scrub(words));
            if (!ok)
                return 2;
        }
        if (!Resolutions.Contains(args.Res))
            throw Out.Stop($"--res {args.Res}: not one of the owner's: {string.Join(", ", Resolutions)}");
        var (plan, notes) = BuildPlan(args.Only, args.BridgeCopy);
        foreach (var n in notes)
            Out.Print(n);
        if (plan.Count == 0)
        {
            Out.Print("Nothing to run.");
            return 1;
        }
        var output = args.Out!;
        using var doer = new Doer(args.Res!, output, args.KeepRaw);
        Out.Print($"HEAD {info.Commit}: {plan.Count} rows at {args.Res}, out {output}");
        HashSet<string> failed;
        var interrupted = false;
        ConsoleCancelEventHandler stop = (_, e) =>
        {
            e.Cancel = true;
            interrupted = true;
        };
        Console.CancelKeyPress += stop;
        try
        {
            failed = Walk(doer, plan);
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }
        if (interrupted)
        {
            Out.Print("Interrupted: writing what there is.");
            failed.Add("interrupted");
        }
        doer.WritePictures(plan, info);
        Out.Print($"Done: {doer.FramesShot.Values.Sum(v => v.Count)} frames; {failed.Count} rows failed or skipped{(failed.Count > 0 ? ": " + string.Join(", ", failed.Order(StringComparer.Ordinal)) : "")}. Index: {Path.Combine(output, "index.html")}");
        Out.Print("Nothing here reads a picture: review each one before it goes into the public docs.");
        return failed.Count == 0 ? 0 : 3;
    }

    static int CmdRemask(Options args, Info info)
    {
        var output = args.Out!;
        var resDir = Path.Combine(output, args.Res!);
        var manifest = (JsonObject)Json.Parse(File.ReadAllText(Path.Combine(resDir, "manifest.json"), Encoding.UTF8))!;
        var was = Renamed;
        Renamed = Json.AsBool(Json.At(manifest, "renamed")) == true;   // the masks of the run that took the frames
        try
        {
            var rows = Rows.ToDictionary(r => r.Id);
            var done = 0;
            foreach (var (rid, got) in (JsonObject)manifest["rows"]!)
                foreach (var f in (Json.At(got, "frames") as JsonArray ?? []).OfType<JsonObject>())
                {
                    var rawFile = Path.Combine(resDir, "_raw", Json.Str(f, "file") ?? "");
                    if (!File.Exists(rawFile))
                        throw Out.Stop($"{rawFile} is not there: --remask needs a run with --keep-raw");
                    var (file, crop) = PostFrame(rawFile, rows[rid], resDir);
                    f["file"] = file;
                    f["crop"] = crop;
                    done++;
                }
            File.WriteAllText(Path.Combine(resDir, "manifest.json"), Json.Dumps(manifest, indent: 1).Replace("\n", Environment.NewLine, StringComparison.Ordinal), new UTF8Encoding(false));
            WriteIndex(output, info);
            Out.Print($"{done} frames masked again at {args.Res}; index {Path.Combine(output, "index.html")}");
            return 0;
        }
        finally
        {
            Renamed = was;
        }
    }

    static void CmdMaskFile(string input, string output, string? rowId)
    {
        var rows = Rows.ToDictionary(r => r.Id);
        if (rowId is null || !rows.TryGetValue(rowId, out var row))
            throw Out.Stop($"no row {rowId ?? "None"}");
        var im = Pictures.Open(input);
        ApplyMasks(im, row);
        Pictures.Save(im, output);
        Out.Print($"{output}: {im.Width}x{im.Height}: {DescribePost(row, im.Width, im.Height)}");
    }

    // ---------------------------------------------------------------- the command line

    public const string Doc = "docshots --list | --dry-run [--res WxH|all] [--only a,b] [--bridge-copy] [--renamed] | --selfcheck | --res WxH --out DIR [--only a,b] [--review-set] "
                              + "[--bridge-copy] [--renamed] [--keep-raw] [--built C] [--remask] | --mask-file IN OUT --row ID   (also: publish, name, merge, maskview, sheets)";

    public static int Command(string[] argv)
    {
        var (args, config) = Harness.TakeConfig(argv);
        if (args.Count > 0 && args[0] is "publish" or "name" or "merge" or "maskview" or "sheets" or "fake-harness")
        {
            var rest = args.Skip(1).ToArray();
            if (args[0] == "fake-harness")
                return FakeHarness(rest);
            if (args[0] == "merge")
                return Merge(rest);
            Init(config);
            return args[0] switch
            {
                "publish" => Publish.Command(rest),
                "name" => DocName(rest, config),
                "maskview" => MaskView(rest),
                _ => Sheets(rest),
            };
        }
        string? res = null, output = null, built = null, picks = null, row = null;
        List<string>? only = null;
        bool list = false, dry = false, selfcheck = false, reviewSet = false, bridgeCopy = false, renamed = false, keepRaw = false, remask = false;
        string[]? maskFile = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            string? inline = null;
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Contains('='))
            {
                inline = a[(a.IndexOf('=') + 1)..];
                a = a[..a.IndexOf('=')];
            }
            string Value()
            {
                if (inline is not null)
                    return inline;
                if (i + 1 >= args.Count)
                    throw new Exit(2, $"usage: {Doc}\ndocshots: error: argument {a}: expected one argument");
                return args[++i];
            }
            switch (a)
            {
                case "--list": list = true; break;
                case "--dry-run": dry = true; break;
                case "--selfcheck": selfcheck = true; break;
                case "--res": res = Value(); break;
                case "--only": only = Value().Split(',').Select(Py.Strip).Where(x => x.Length > 0).ToList(); break;
                case "--review-set": reviewSet = true; break;
                case "--out": output = Value(); break;
                case "--bridge-copy": bridgeCopy = true; break;
                case "--built": built = Value(); break;
                case "--picks": picks = Value(); break;
                case "--renamed": renamed = true; break;
                case "--keep-raw": keepRaw = true; break;
                case "--remask": remask = true; break;
                case "--row": row = Value(); break;
                case "--mask-file":
                    if (i + 2 >= args.Count)
                        throw new Exit(2, $"usage: {Doc}\ndocshots: error: argument --mask-file: expected 2 arguments");
                    maskFile = [args[i + 1], args[i + 2]];
                    i += 2;
                    break;
                case "-h":
                case "--help":
                    Out.Print("usage: " + Doc);
                    return 0;
                default:
                    throw new Exit(2, $"usage: {Doc}\ndocshots: error: unrecognized arguments: {a}");
            }
        }
        Init(config);
        Renamed = renamed;
        Built = built ?? "";
        if (picks is not null)
            PicksPath = picks;
        if (reviewSet)
            only = [.. only ?? [], .. ReviewSet];
        var info = Validate();
        if (maskFile is not null)
        {
            CmdMaskFile(maskFile[0], maskFile[1], row);
            return 0;
        }
        if (list)
        {
            WarnProblems(info);
            CmdList(info);
            return 0;
        }
        if (selfcheck)
        {
            RequireValid(info);
            return CmdSelfCheck(info);
        }
        if (dry)
        {
            WarnProblems(info);
            var resList = res == "all" ? Resolutions : [res ?? Resolutions[0]];
            CmdDryRun(resList, only, bridgeCopy, output ?? Path.Combine(Work, "out"));
            return 0;
        }
        if (res is null || output is null)
            throw new Exit(2, $"usage: {Doc}\ndocshots: error: the real run takes --res and --out; or --list, --dry-run, --selfcheck");
        var options = new Options(res, output, only, bridgeCopy, keepRaw);
        if (remask)
            return CmdRemask(options, info);
        RequireValid(info);
        return CmdRun(options, info);
    }
}
