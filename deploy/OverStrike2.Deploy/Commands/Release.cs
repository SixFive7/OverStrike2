using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>A step of a release refused or failed: what is printed, and the exit code 1.</summary>
sealed class ReleaseStop(string message) : Exception(message);

/// <summary>A release in two commands: `live` puts a new bridge and plugin on the server, `publish` makes a content update of the HUD item
/// (deploy/release.py, ported). It drives the other commands with their private config, so it holds no credential of its own: the Docker
/// API key comes from the environment, the RCON password from the game container's own environment (CS2_RCONPW), never printed.
///
///     release live &lt;bridge-tag&gt; [--no-plugin] [--under-lock] [--dry-run]
///     release publish --note "&lt;what changed&gt;" --client-test &lt;run folder&gt; [--under-lock] [--dry-run] [--wait-minutes N]
///
/// live - after the release passed its tests (and after publish, when it has HUD content):
///   1. 0 humans: A2S twice, 10 s apart, and RCON's status;
///   2. the server lock taken (or, with --under-lock, the one already held for this release used and left held);
///   3. the bridge swapped to that exact image tag by the bridge command's own swap: nobody is disconnected, a failed swap puts the old one back;
///   4. the plugin shipped (plugin ship --apply: the build that was tested - nothing is built here);
///   5. the maintenance flag on, 0 humans once more, one RCON quit;
///   6. the new engine: a new container start, A2S answering, OverStrike2 &lt;version&gt; LOADED, the files in the game container those of the
///      build (sha256), and the HUD item (hud/workshop_id.txt) among the addons the server hands out;
///   7. the flag off - whatever happened - the deployment's check (check verify, or release.verify), the lock released (kept after a
///      failure, and the way back printed);
///   8. the record's lines, for the deployment's own log.
///   --no-plugin: steps 1-3 and 7 only - the swap alone, nothing restarts.
///
/// publish - a content update of the one HUD item (a new item is never created):
///   1. --client-test names the real-client test's run folder (with its shots/): the update ships only once a real client saw it work;
///   2. the server lock, as for live;
///   3. the held-item check on the live server: os_hud page 3999999999 must read "NOT shown" in the plugin's log (asked as css_gghud too when
///      the answer is not the plugin's os_hud: - a plugin from before release 191);
///   4. workshop hud-publish --note ... --user-approved-update (hud/build.ps1 first, by hand);
///   5. the item's time_updated from the keyless Web API, one call at a time, a minute apart, until it moves (the upload) and moves again
///      (Valve's approval) or --wait-minutes pass.
///
/// --dry-run: every check that only reads; nothing that writes - no lock, no swap, no copy, no flag, no quit, no upload.</summary>
sealed class Release
{
    public const string Owner = "Over Strike 2 release (os2-deploy release)";
    public const string PluginDir = "/home/steam/cs2-dedicated/game/csgo/addons/counterstrikesharp/plugins/" + Plugin.Name;
    const string CssLogs = "/home/steam/cs2-dedicated/game/csgo/addons/counterstrikesharp/logs";
    public const string NoSuchItem = "3999999999";
    static readonly Regex PlayersRx = new(@"^\s*players\s*:\s*([0-9]+)\s+humans,\s*([0-9]+)\s+bots", RegexOptions.Multiline);   // CS2 1.41's status
    static readonly string[] AddonLists = ["mm_extra_addons", "mm_client_extra_addons"];   // mounted by the server; sent to the clients alone
    const string Details = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    public const string InnerSha = "import hashlib,os,sys,json\nd=sys.argv[1]\nprint(json.dumps({f:hashlib.sha256(open(os.path.join(d,f),'rb').read()).hexdigest()"
                                   + " for f in sys.argv[2:] if os.path.isfile(os.path.join(d,f))}))";
    public const string InnerGrep = "import glob,os,sys\nfs=sorted(glob.glob(os.path.join(sys.argv[1],'*.txt')),key=os.path.getmtime)[-2:]\n"
                                    + "print('\\n'.join(l.rstrip() for f in fs for l in open(f,errors='replace') if sys.argv[2] in l))";

    readonly IConfig cfg;
    readonly bool dry;
    public readonly IDocker D;
    readonly Func<int?> humansA2s;
    readonly Func<List<string>, int> run;
    readonly Action<double> sleep;
    readonly Func<string, JsonObject?> fetch;
    readonly string game, bridgeName, repoName;
    public readonly string Lock;
    readonly string flag;
    readonly object? verifyWith;
    public bool TookLock, SetFlag;
    public readonly List<string> Record = [];

    // seams the self-tests replace
    public string RepoDir = Repo.Root;
    public Func<Dictionary<string, string>> PluginFiles = PluginFilesOf;
    public Func<string> PluginVersion = PluginVersionOf;
    public Func<string, string, int>? Swap = null;

    public Release(IConfig cfg, IDocker? d = null, Func<int?>? humans = null, Func<List<string>, int>? run = null, Action<double>? sleep = null,
                   Func<string, JsonObject?>? fetch = null, bool dry = false)
    {
        this.cfg = cfg;
        this.dry = dry;
        D = d ?? new Docker(cfg);
        humansA2s = humans ?? (() => A2s.Humans(cfg.Str("server.address", required: true)));
        this.run = run ?? (argv => Proc.Run(argv, cwd: Repo.Root, env: ChildEnv(cfg)));
        this.sleep = sleep ?? (s => Thread.Sleep(TimeSpan.FromSeconds(s)));
        this.fetch = fetch ?? FetchDetails;
        game = cfg.Str("bridge.game_container", required: true);
        bridgeName = cfg.Str("bridge.container", required: true);
        repoName = cfg.Str("bridge.image", "localhost/overstrike2-bridge");
        Lock = cfg.Str("server.lock_file");
        flag = Path.Combine(cfg.Str("bridge.data_dir", required: true), "maintenance");
        var configured = cfg.Get("release.verify", null);
        verifyWith = ConfigExtensions.Truthy(configured) ? configured
            : (List<string>)[.. Proc.Self(), "check", "verify", .. cfg.Path is not null ? new[] { "--config", Path.GetFullPath(cfg.Path) } : []];
    }

    static void Log(string text) => Out.Log(text);

    // ---- reads

    string Rcon(string command)
    {
        var g = D.Inspect("containers", game) ?? [];
        var pw = InspectOf.RconPassword(g) is { Length: > 0 } p ? p : InspectOf.RconPassword(null, D.Inspect("containers", bridgeName));
        var port = (g.Count > 0 ? InspectOf.EnvOf(g, "RCON_PORT") : "") is { Length: > 0 } rp ? rp : "27015";
        return D.ExecIn(game, ["python3", "-c", Bridge.InnerRcon, pw, command, port], timeout: 30);
    }

    /// <summary>0 humans by A2S twice, 10 s apart (a map change shows 0 for a moment), and by RCON's status where it parses.</summary>
    void Nobody(string why)
    {
        var first = humansA2s();
        sleep(10);
        var second = humansA2s();
        var m = PlayersRx.Match(Rcon("status") ?? "");
        int? byRcon = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        Log($"humans {why}: A2S {Py.Str(first)}, {Py.Str(second)}; RCON status {(byRcon is not null ? byRcon.Value.ToString(CultureInfo.InvariantCulture) : "unreadable (A2S decides)")}");
        if (first != 0 || second != 0 || byRcon is > 0 or < 0)
            throw new ReleaseStop($"REFUSED: somebody is on (A2S {Py.Str(first)}, {Py.Str(second)}, RCON {Py.Str(byRcon)}) - a release happens only at 0 humans");
    }

    string? LockText() => Lock.Length > 0 && File.Exists(Lock) ? File.ReadAllText(Lock, Encoding.UTF8) : null;

    // ---- the lock and the flag

    void TakeLock(bool underLock, string purpose)
    {
        if (Lock.Length == 0)
        {
            Log("no server.lock_file in the private config - no lock taken");
            return;
        }
        var held = LockText();
        if (held is not null)
        {
            if (!underLock)
                throw new ReleaseStop("REFUSED: the server lock is held - " + Repo.Oneline(held) + " (--under-lock if it is this release's)");
            Log("server lock held for this release, used and left held: " + (Py.SplitLines(held) is { Count: > 0 } l ? l[0] : ""));
            return;
        }
        if (underLock)
            throw new ReleaseStop("REFUSED: --under-lock, but the server lock is free");
        if (dry)
        {
            Log($"dry run: would take the server lock ({Lock})");
            return;
        }
        var now = Out.Clock();
        var words = $"owner: {Owner}\nsince: {now.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n"
                    + $"expected end: {now.AddSeconds(1800).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\npurpose: {purpose}\n";
        using (var fs = new FileStream(Lock, FileMode.CreateNew, FileAccess.Write))
            fs.Write(Encoding.UTF8.GetBytes(words));
        TookLock = true;
        Log("server lock taken: " + purpose);
    }

    void DropLock()
    {
        if (TookLock && (LockText() ?? "").StartsWith($"owner: {Owner}", StringComparison.Ordinal))
        {
            File.Delete(Lock);
            TookLock = false;
            Log("server lock released");
        }
    }

    void FlagOn(string why)
    {
        if (Path.Exists(flag))
        {
            Log("maintenance flag already set by someone else - left as it is");
            return;
        }
        using (var fs = new FileStream(flag, FileMode.CreateNew, FileAccess.Write))
            fs.Write(Encoding.UTF8.GetBytes($"{Owner}: {why}, {Out.Clock().ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)}\n"));
        SetFlag = true;
        Log("maintenance flag on; 12 s for a map change already counting down");
        sleep(12);
    }

    void FlagOff()
    {
        if (!SetFlag)
            return;
        try
        {
            File.Delete(flag);
            Log("maintenance flag off");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log($"WARNING: the maintenance flag could not be removed ({error.Message}) - remove {flag} by hand");
        }
        SetFlag = false;
    }

    // ---- live

    public int Live(string tag, bool shipPlugin, bool underLock)
    {
        var target = D.Inspect("images", $"{repoName}:{tag}");
        if (target is null || target.Count == 0)
            throw new ReleaseStop($"REFUSED: no image {repoName}:{tag} on the server");
        var running = D.Inspect("containers", bridgeName) ?? [];
        var labels = Json.Obj(target, "Config", "Labels");
        var rev = labels.TryGetPropertyValue("org.opencontainers.image.revision", out var rv) ? Json.PyStr(rv) : "?";
        var targetId = Json.Str(target, "Id") ?? "";
        Log($"bridge: {repoName}:{tag} = {Py.Slice(targetId, 7, 19)} (revision {rev}); running now {Py.Slice(Json.PyStr(running["Image"]), 7, 19)}");
        string? version = null;
        var files = new Dictionary<string, string>();
        if (shipPlugin)
        {
            version = PluginVersion();
            files = PluginFiles();
            if (files.Count == 0)
                throw new ReleaseStop($"REFUSED: no plugin build in {Plugin.OutDir} - os2-deploy plugin build first (the build that was tested)");
            Log($"plugin: {Plugin.Name} {version}, {files.Count} file(s), {Plugin.Name}.dll sha256 {Py.Slice(files.GetValueOrDefault(Plugin.Name + ".dll", "?"), 0, 12)}");
        }
        Nobody("before the release");
        var hud = WorkshopItem();
        if (dry)
        {
            TakeLock(underLock, "dry run");
            Log($"dry run: would swap the bridge to {repoName}:{tag}" + (shipPlugin ? $", ship plugin {version}, set the flag, quit the engine once" : ""));
            return 0;
        }
        TakeLock(underLock, $"release: bridge {tag}" + (shipPlugin ? $", plugin {version}" : ""));
        var t0 = Out.Clock().ToString("HH:mm", CultureInfo.InvariantCulture);
        var ok = false;
        try
        {
            if (targetId != Json.Str(running, "Image") || !Json.Truthy(Json.At(running, "State", "Running")))
            {
                int r;
                try
                {
                    r = Swap is not null ? Swap(tag, "previous") : new Bridge(D, repoName, bridgeName, game, cfg).Swap(tag, "previous");
                }
                catch (Exit e)   // the bridge's refusals end the command: here a step that failed, not this command's end
                {
                    r = e.Code;
                }
                if (r != 0)
                    throw new ReleaseStop("the bridge swap failed - the bridge command put the old one back (its lines above)");
            }
            var now = D.Inspect("containers", bridgeName) ?? [];
            Record.Add($"bridge image `{Json.PyStr(now["Image"])}` (revision {rev}) by `os2-deploy release live {tag}`");
            if (shipPlugin)
            {
                if (run([.. Proc.Self(), "plugin", "ship", "--apply"]) != 0)
                    throw new ReleaseStop("os2-deploy plugin ship --apply failed - nothing restarted; the server may hold part of the new plugin");
                Record.Add($"plugin {version}, `{Plugin.Name}.dll` sha256 `{Py.Slice(files.GetValueOrDefault(Plugin.Name + ".dll", "?"), 0, 12)}...` shipped");
                FlagOn($"release, plugin {version}");
                Nobody("right before the quit");
                var started = Json.Str(D.Inspect("containers", game), "State", "StartedAt");
                Log("RCON quit");
                try
                {
                    Rcon("quit");
                }
                catch (Exception error) when (error is not ReleaseStop and not Exit)   // the engine and the exec in it end together: no answer is the normal case
                {
                    Log($"(the quit's exec ended without an answer: {error.GetType().Name})");
                }
                Record.Add("maintenance flag on, one RCON `quit`");
                Back(started, version!, files, hud);
            }
            ok = true;
        }
        finally
        {
            FlagOff();
            if (shipPlugin && Record.Count > 0)
                Record.Add("flag off");
        }
        var verdict = Verify();
        Record.Add($"the deployment's check: {verdict}");
        if (verdict != "ALL GOOD")
            ok = false;
        if (ok)
            DropLock();
        Log($"RECORD - live ({t0}-{Out.Clock().ToString("HH:mm", CultureInfo.InvariantCulture)}, under the lock, 0 humans): " + string.Join("; ", Record));
        return ok ? 0 : 1;
    }

    /// <summary>{list: the Workshop ids in it} for the two addon lists, as the running server holds them (null: unreadable).</summary>
    Dictionary<string, List<string>?> Addons()
    {
        var output = new Dictionary<string, List<string>?>();
        foreach (var name in AddonLists)
        {
            var m = Regex.Match(Rcon(name) ?? "", Regex.Escape(name) + @"""?\s*=\s*""?([0-9,]*)");
            output[name] = m.Success ? m.Groups[1].Value.Split(',').Where(i => i.Length > 0).ToList() : null;
        }
        return output;
    }

    /// <summary>The new engine after the quit: a new container start, A2S, the plugin LOADED, its files, the HUD addon.</summary>
    void Back(string? started, string version, Dictionary<string, string> files, string hud)
    {
        var restarted = false;
        for (var k = 0; k < 36 && !restarted; k++)   // 3 min for the container to start again (pre.sh then installs, organizes, starts the engine)
        {
            sleep(5);
            var s = Json.Str(D.Inspect("containers", game), "State", "StartedAt");
            restarted = !string.IsNullOrEmpty(s) && s != started;
        }
        if (!restarted)
            throw new ReleaseStop("the game container did not start again within 3 minutes of the quit - look now");
        var answered = false;
        for (var k = 0; k < 60 && !answered; k++)    // 10 min for A2S
        {
            sleep(10);
            answered = humansA2s() is not null;
        }
        if (!answered)
            throw new ReleaseStop("A2S has not answered for 10 minutes after the restart - look now");
        var plugins = "";
        var quoted = "\"" + Plugin.Name + "\"";
        for (var k = 0; k < 18; k++)                 // 3 min for the plugins
        {
            plugins = Rcon("css_plugins list") ?? "";
            if (Py.SplitLines(plugins).Any(l => l.Contains(quoted, StringComparison.Ordinal) && l.Contains("LOADED", StringComparison.Ordinal)))
                break;
            sleep(10);
        }
        var line = Py.SplitLines(plugins).Where(l => l.Contains(quoted, StringComparison.Ordinal)).Select(Py.Strip).FirstOrDefault() ?? "";
        if (!line.Contains("LOADED", StringComparison.Ordinal) || !line.Contains(version, StringComparison.Ordinal))
            throw new ReleaseStop($"{Plugin.Name} {version} is not LOADED: {(line.Length > 0 ? line : "not listed")}");
        var output = D.ExecIn(game, ["python3", "-c", InnerSha, PluginDir, .. files.Keys.Order(StringComparer.Ordinal)]);
        JsonObject there;
        try
        {
            var last = Py.SplitLines(Py.Strip(output));
            there = Json.Parse(last.Count > 0 ? last[^1] : throw new JsonSyntaxError("no output")) as JsonObject ?? throw new JsonSyntaxError("no object");
        }
        catch (JsonSyntaxError)
        {
            throw new ReleaseStop($"the plugin's files in the game container could not be read: {Py.Slice(Py.Strip(output), 0, 200)}");
        }
        var wrong = files.Keys.Where(f => Json.AsStr(there[f]) != files[f]).Order(StringComparer.Ordinal).ToList();
        if (wrong.Count > 0)
            throw new ReleaseStop($"the game container's plugin files are not the build's: {string.Join(", ", wrong)}");
        var lists = Addons();
        var shown = string.Join(", ", lists.Select(kv => $"`{kv.Key} = {(kv.Value is not null ? string.Join(",", kv.Value) : "?")}`"));
        if (hud.Length > 0 && !lists.Values.Any(v => (v ?? []).Contains(hud)))
            throw new ReleaseStop($"the server hands out no HUD item {hud}: {shown}");
        Record.Add($"{line}; {shown}; the shipped files' hashes those of the build");
        Log($"back: {line}, {shown}, {files.Count} file(s) as built");
    }

    /// <summary>The deployment's check (check verify, or release.verify): "ALL GOOD", "LOOK ...", or "NOT RUN" (its script is missing: a
    /// failure).</summary>
    string Verify()
    {
        var argv = verifyWith is List<object?> list ? list.Select(Py.Str).ToList()
                 : verifyWith is List<string> mine ? mine
                 : ["python", "-I", Py.Str(verifyWith)];
        var script = argv.Skip(1).FirstOrDefault(a => a.EndsWith(".py", StringComparison.Ordinal));
        if (script is not null && !File.Exists(script))
        {
            Log($"release.verify: {script} not found - check the server by hand");
            return "NOT RUN";
        }
        return run(argv) == 0 ? "ALL GOOD" : "LOOK (its lines above)";
    }

    // ---- publish

    public int Publish(string? note, string? clientTest, bool underLock, int waitMinutes)
    {
        if (string.IsNullOrEmpty(clientTest) || !Directory.Exists(Path.Combine(clientTest, "shots")))
            throw new ReleaseStop("REFUSED: --client-test must name the real-client test's run folder, with its shots/ - a HUD update ships only "
                                  + "once a real client saw it work");
        if (string.IsNullOrEmpty(note))
            throw new ReleaseStop("REFUSED: --note, the change note Steam shows with the update");
        var hud = WorkshopItem();
        var before = fetch(hud);
        Log($"the HUD item {hud} now: {Describe(before)}");
        TakeLock(underLock, "release: HUD content update");
        try   // publish changes nothing on the server: the lock it took is released whatever happens
        {
            var seen = HeldCheck();
            if (dry)
            {
                Log($"dry run: would publish a content update of {hud} with the note {Py.Repr(note)}, then wait for time_updated to move twice");
                return 0;
            }
            if (run([.. Proc.Self(), "workshop", "hud-publish", "--note", note, "--user-approved-update"]) != 0)
                throw new ReleaseStop("os2-deploy workshop hud-publish failed (its lines above)");
            var moves = new List<long>();
            var last = Json.AsLong(before?["time_updated"]) ?? 0;
            for (var k = 0; k < waitMinutes; k++)      // one call a minute, each after the last has ended: one at a time
            {
                if (moves.Count == 2)
                    break;
                sleep(60);
                var now = fetch(hud);
                var t = Json.AsLong(now?["time_updated"]) ?? 0;
                if (now is not null && t > last)
                {
                    last = t;
                    moves.Add(last);
                    Log((moves.Count == 1 ? "the upload" : "Valve's approval, most likely") + $": time_updated {Hm(last)} - {Describe(now)}");
                }
            }
            if (moves.Count < 2)
                throw new ReleaseStop($"time_updated moved {moves.Count} time(s) in {waitMinutes} min - "
                                      + (moves.Count == 0 ? "no upload seen" : "the update waits in Valve's queue; look again later (hud-query)"));
            Log($"RECORD - publish: a content update of {hud}, client test {Path.GetFileName(clientTest.TrimEnd(Path.DirectorySeparatorChar))}; the held-item "
                + $"check: `{seen}`; time_updated {Hm(moves[0])} (the upload), then {Hm(moves[1])}: Valve's approval, "
                + $"{(long)Math.Floor((moves[1] - moves[0]) / 60.0)} minutes after the upload");
            return 0;
        }
        finally
        {
            DropLock();
        }
    }

    /// <summary>os_hud in a plugin from before release 191, which a release's held-item check may still find live.</summary>
    public const string OldHudCommand = "css_gghud";

    /// <summary>os_hud page &lt;an id that does not exist&gt;: the plugin's verdict, from its log a few seconds later, must be NOT shown. This runs against
    /// the plugin that is live before a release ships its own: one from before release 191 knows the command only by its old word, css_gghud. So
    /// whenever the reply is not the plugin's own answer (it begins os_hud:) - an unknown command, however the engine words it, or no answer at
    /// all - the check asks by that word too (OldHudCommand). A reply decides nothing: the verdict is the plugin's log line.</summary>
    string HeldCheck()
    {
        var key = $"HUD item probe: {NoSuchItem}";
        List<string> Grep() => Py.SplitLines(D.ExecIn(game, ["python3", "-c", InnerGrep, CssLogs, key]) ?? "").Where(l => l.Contains(key, StringComparison.Ordinal)).ToList();
        var old = Grep().Count;
        var word = "os_hud";
        var reply = Py.Strip(Rcon($"{word} page {NoSuchItem}") ?? "");
        if (!reply.Contains("os_hud:", StringComparison.Ordinal))
        {
            Log($"held-item check: {word} page {NoSuchItem} -> {(reply.Length > 0 ? Py.Slice(reply, 0, 160) : "(no answer)")} - not the plugin's os_hud: answer, asked as {OldHudCommand} too");
            word = OldHudCommand;
            reply = Py.Strip(Rcon($"{word} page {NoSuchItem}") ?? "");
        }
        Log($"held-item check: {word} page {NoSuchItem} -> {Py.Slice(reply, 0, 160)}");
        for (var k = 0; k < 12; k++)
        {
            sleep(5);
            var lines = Grep();
            if (lines.Count > old)
            {
                var after = lines[^1];
                var seen = after[(after.IndexOf(key, StringComparison.Ordinal) + key.Length)..].Trim(' ', ':');
                Log("held-item check: " + seen);
                if (!seen.Contains("-> NOT shown", StringComparison.Ordinal))
                    throw new ReleaseStop("REFUSED: the held-item check does not read NOT shown - the server would not recognise an item under review");
                return seen;
            }
        }
        throw new ReleaseStop("REFUSED: the held-item check's answer did not reach the plugin's log within a minute");
    }

    /// <summary>The environment of the commands a release runs (plugin ship, workshop, the check): this one's, with OVERSTRIKE2_CONFIG naming
    /// the config the release itself runs on - absolute, since they run in the repository's root. null (this environment as it is) for a
    /// config without a path.</summary>
    public static Dictionary<string, string?>? ChildEnv(IConfig cfg)
    {
        if (cfg.Path is null)
            return null;
        var env = Proc.Environment();
        env["OVERSTRIKE2_CONFIG"] = Path.GetFullPath(cfg.Path);
        return env;
    }

    /// <summary>The one HUD item, hud/workshop_id.txt: what a release's server must hand out, and what a content update goes to.</summary>
    public string WorkshopItem()
    {
        var hud = Py.Strip(File.ReadAllText(Path.Combine(RepoDir, "hud", "workshop_id.txt"), Encoding.UTF8));
        if (!Py.IsDigits(hud))
            throw new ReleaseStop("REFUSED: hud/workshop_id.txt names no item - there is no create path");
        return hud;
    }

    static string PluginVersionOf()
    {
        var src = File.ReadAllText(Path.Combine(Plugin.Project, Plugin.Name + ".cs"), Encoding.UTF8);
        var m = Regex.Match(src, @"public const string Version = ""([^""]+)""");
        return m.Success ? m.Groups[1].Value : "?";
    }

    /// <summary>{file: sha256} of what plugin ship ships, from its build output; {} without a build.</summary>
    static Dictionary<string, string> PluginFilesOf()
    {
        if (!File.Exists(Path.Combine(Plugin.OutDir, Plugin.Name + ".dll")))
            return [];
        return Plugin.ShipList().ToDictionary(f => f, f => Repo.Sha256(Path.Combine(Plugin.OutDir, f)));
    }

    /// <summary>The item's details from the keyless Web API (one call; the caller keeps them a minute apart), or null.</summary>
    static JsonObject? FetchDetails(string item)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var body = new FormUrlEncodedContent([new("itemcount", "1"), new("publishedfileids[0]", item)]);
            using var resp = http.PostAsync(Details, body).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            var json = Json.Parse(resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            return Json.Arr(json, "response", "publishedfiledetails") is { Count: > 0 } a ? a[0] as JsonObject : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonSyntaxError or IOException)
        {
            return null;
        }
    }

    static string Hm(long t) => t != 0 ? DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "never";

    static string Describe(JsonObject? d)
    {
        if (d is null || d.Count == 0)
            return "no answer";
        return $"result {Json.PyStr(d["result"])}, visibility {Json.PyStr(d["visibility"])}, file_size {Json.PyStr(d["file_size"])}, time_updated {Hm(Json.AsLong(d["time_updated"]) ?? 0)}";
    }

    public static int Command(string[] args)
    {
        var a = new Args(args, "release live <bridge-tag> [--no-plugin] [--under-lock] [--dry-run] | release publish --note N --client-test RUN [--wait-minutes N] [--under-lock] [--dry-run] [--config FILE]",
                         flags: ["--no-plugin", "--under-lock", "--dry-run"], options: ["--note", "--client-test", "--wait-minutes", "--config"]);
        var command = a.Choice(["live", "publish"]);
        var tag = a.Positional();
        a.Done();
        var waitMinutes = a.IntOpt("--wait-minutes", 120);
        var rel = new Release(new Config(a.Opt("--config")), dry: a.Flag("--dry-run"));
        try
        {
            if (command == "live")
            {
                if (string.IsNullOrEmpty(tag))
                    throw new ReleaseStop("live needs the bridge image tag");
                return rel.Live(tag, !a.Flag("--no-plugin"), a.Flag("--under-lock"));
            }
            return rel.Publish(a.Opt("--note"), a.Opt("--client-test"), a.Flag("--under-lock"), waitMinutes);
        }
        catch (ReleaseStop stop)
        {
            Out.Log(stop.Message);
            if (rel.TookLock)
                Out.Log($"the server lock stays held ({rel.Lock}) - the way back: `os2-deploy bridge rollback` for the bridge "
                        + "(:previous is what this run replaced), the previous plugin build shipped again and one quit at 0 humans; then remove the lock");
            return 1;
        }
    }
}
