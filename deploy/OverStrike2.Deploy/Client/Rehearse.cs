using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace OverStrike2.Deploy;

/// <summary>The release rehearsal: production is set aside, the candidate - this checkout's builds, a bridge image tag and the stack's compose
/// file - runs as a throwaway stack on production's network, IPs and MACs, and everything comes back. Under the harness's server lock, with
/// nobody on. Prints no secret.
///
///     rehearse prep                   the lock held; 0 humans; production's game server, bridge and sync aside (testcopy prep and bridge-prep,
///                                     the sync stopped and renamed); its records backed up; the files staged
///     rehearse backup                 only the records: os2-server's state in the game's tree (update-watch), its listings, the Workshop manifest
///     rehearse stage                  only the files, in the rehearsal folder: os2-server, os2-sync and pre.sh (server build's), the mode cfgs,
///                                     the plugin (plugin build's), a copy of the bridge's data; the sync's state copied and its work folder made
///     rehearse up [--daily HH:MM] [--via env|file] [--tag T] [--compose FILE]
///                                     the stack: the compose file (stack.repo/stack.file, or FILE) with production's folders replaced by the
///                                     rehearsal's, a fresh test secret, the bridge on &lt;bridge.image&gt;:T (candidate), the test labels, and the
///                                     settings: maps.delete dryrun, restarts.daily, hud.itemPage off, and the sync isolated - paths.syncState
///                                     and paths.syncWork on copies, sync.testPort, sync.handover and sync.publish off, no mail
///     rehearse compose [...]          read-only: what up would create, written to the harness's state folder, its changes named
///     rehearse down                   the stack gone (a container it leaves behind removed only if it carries the test label)
///     rehearse restore                records back, production's three containers back (testcopy's checks: its endpoint, its own plugins)
///     rehearse clean                  only at the very end, the stack down (after restore too): the rehearsal folder, the sync's copies and the
///                                     backup gone
///     rehearse logs &lt;cs2|bridge|sync&gt; &lt;seconds&gt; [regex] | sh &lt;cs2|bridge|sync&gt; "&lt;cmd&gt;" [user] | rcon "&lt;cmd&gt;"... | ship &lt;file&gt; [name] | status
///
/// Where things are comes from production's own inspects, saved at prep: the game's tree (its bind at paths.gameDir), the bridge's data
/// (its /data), the plugins folder (paths.plugins), the program folder and pre.sh, the mode cfgs, the sync's state and work folders, the
/// network. The rehearsal folder is &lt;the bridge's data folder's parent&gt;/rehearsal unless rehearse.dir names another; the stack manager is
/// rehearse.manager and rehearse.endpoint.</summary>
sealed class Rehearse
{
    const string Purpose = "the release rehearsal";
    const string ProgramAt = "/home/steam/overstrike2";
    const string LauncherAt = "/home/steam/cs2-dedicated/pre.sh";
    const string SyncStateAt = "/home/steam/sync-state", SyncWorkAt = "/home/steam/workshop-sync";
    static readonly Regex CfgAt = new(@"/gamemode_\w+_server\.cfg$");

    readonly TestCopy tc;
    readonly Harness h;
    readonly string syncName, syncAside, secretFile;
    public readonly Dictionary<string, string> Names;

    public Rehearse(TestCopy tc)
    {
        this.tc = tc;
        h = tc.H;
        syncName = h.Cfg.Str("bridge.sync_container");
        syncAside = syncName + "-prod-aside";
        Names = new Dictionary<string, string> { ["cs2"] = tc.Game, ["bridge"] = tc.Bridge, ["sync"] = syncName };
        secretFile = Path.Combine(tc.State, "rehearsal-secret.txt");
        ManagerCall = RealManagerCall;
    }

    string SyncSaved => Path.Combine(tc.State, "sync-inspect.json");
    static void Log(params object?[] parts) => Out.Log(parts);

    // ------------------------------------------------------------------------------------------------- where things are

    JsonObject Saved(string path, string what) =>
        File.Exists(path) ? Json.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? [] : throw Out.Stop($"REFUSED: {what} was not saved - prep first");

    JsonObject Game() => Saved(tc.ProdSaved, "production's game server inspect");
    JsonObject BridgeInspect() => Saved(tc.BridgeSaved, "production's bridge inspect");
    JsonObject? SyncInspect() => syncName.Length > 0 ? Saved(SyncSaved, "production's sync inspect") : null;

    /// <summary>Production's container where clean needs it: the inspect saved at prep while production is aside; once restore has put
    /// production back - and deleted the saved inspects, which hold secrets - the container itself as it runs now. Never the rehearsal's.</summary>
    JsonObject Production(string saved, string name, string what)
    {
        if (File.Exists(saved))
            return Saved(saved, what);
        var live = tc.Inspect(name) ?? throw Out.Stop($"REFUSED: {what} was not saved and {name} does not exist - look");
        return TestCopy.IsTest(live) ? throw Out.Stop($"REFUSED: {name} is the rehearsal's - down and restore first") : live;
    }

    string Need(string? value, string what) => !string.IsNullOrEmpty(value) ? value : throw Out.Stop($"REFUSED: production has no bind for {what}");

    string GameTree(JsonObject game) => Need(TestCopy.BindSource(game, SettingPath(game, "gameDir", TestCopy.GameDirAt)), "the game's tree");
    string BridgeData(JsonObject bridge) => Need(TestCopy.DataMount(bridge), "the bridge's data");

    /// <summary>The rehearsal's folder on the host: rehearse.dir, else beside the bridge's data folder.</summary>
    string Dir(JsonObject bridge)
    {
        var configured = h.Cfg.Str("rehearse.dir");
        var dir = configured.Length > 0 ? configured : BridgeData(bridge).TrimEnd('/')[..BridgeData(bridge).TrimEnd('/').LastIndexOf('/')] + "/rehearsal";
        if (!dir.StartsWith('/') || dir.Split('/').Contains("..") || dir.TrimEnd('/').Count(c => c == '/') < 2)
            throw Out.Stop($"REFUSED: {Py.Repr(dir)} is no place for the rehearsal's folder");
        return dir.TrimEnd('/');
    }

    static string SettingPath(JsonNode? d, string key, string dflt)
    {
        var text = InspectOf.EnvOrNull(d, Check.Content);
        string? value = null;
        if (text is not null)
        {
            try
            {
                value = Json.AsStr(Json.At(Json.At(Json.Jsonc(text), "paths"), key));
            }
            catch (JsonSyntaxError)
            {
            }
        }
        return value is not null && value.StartsWith('/') ? value.TrimEnd('/') : dflt;
    }

    /// <summary>Production's host folders the rehearsal replaces, each with the rehearsal's own: the bridge's data, the plugins folder, the
    /// program folder, pre.sh and every mode cfg.</summary>
    Dictionary<string, string> Replacements(JsonObject game, JsonObject bridge)
    {
        var dir = Dir(bridge);
        var map = new Dictionary<string, string>(StringComparer.Ordinal) { [BridgeData(bridge)] = dir + "/bridge" };
        if (TestCopy.BindSource(game, TestCopy.PluginsAtOf(game)) is { Length: > 0 } plugins)
            map[plugins] = dir + "/plugins";
        if (TestCopy.BindSource(game, ProgramAt) is { Length: > 0 } program)
            map[program] = dir + "/overstrike2";
        if (TestCopy.BindSource(game, LauncherAt) is { Length: > 0 } launcher)
            map[launcher] = dir + "/overstrike2/pre.sh";
        foreach (var (src, target) in Binds(game))
            if (CfgAt.IsMatch(target))
                map[src] = $"{dir}/cfg/{target[(target.LastIndexOf('/') + 1)..]}";
        return map;
    }

    /// <summary>(host source, container target) of every bind a container has.</summary>
    static List<(string Source, string Target)> Binds(JsonNode? d)
    {
        var output = new List<(string, string)>();
        foreach (var b in Json.Arr(d, "HostConfig", "Binds").Select(Json.PyStr))
        {
            var parts = b.Split(':');
            if (parts.Length >= 2)
                output.Add((parts[0], parts[1]));
        }
        foreach (var m in Json.Arr(d, "HostConfig", "Mounts"))
            if (Json.Str(m, "Type") == "bind")
                output.Add((Json.Str(m, "Source") ?? "", Json.Str(m, "Target") ?? ""));
        return output;
    }

    // ------------------------------------------------------------------------------------------------------------- the verbs

    void Backup()
    {
        h.NeedLock();
        var game = Game();
        var (code, output) = tc.Helper($"{tc.Game}-rehearsal-backup", [$"{GameTree(game)}:/g"],
            "set -e; U=/g/update-watch; B=$U/rehearsal-backup; mkdir -p $B; for f in state.json keep.json installed.json; do [ -f $U/$f ] && cp -p $U/$f $B/; done; "
            + "ls -la --time-style=+%s $U > $B/listing.txt; ls $U/steamlogs > $B/steamlogs.txt 2>/dev/null || true; "
            + "A=/g/game/bin/linuxsteamrt64/steamapps/workshop/appworkshop_730.acf; [ -f $A ] && cp -p $A $B/appworkshop_730.acf; "
            + "cd $B && ls; sha256sum $U/state.json $U/keep.json 2>/dev/null | cut -c1-16,65-");
        Log("backup:", code, output.Replace("\n", " | "));
        if (code != 0)
            throw Out.Stop("REFUSED: the backup failed");
    }

    void SyncAside()
    {
        if (syncName.Length == 0)
            return;
        var d = tc.Inspect(syncName);
        if (d is not null && tc.Inspect(syncAside) is null)
        {
            File.WriteAllText(SyncSaved, Json.Dumps(d, indent: 1));
            if (Json.Str(d, "State", "Status") != "exited")
                tc.StopContainer(syncName, 60);
            var st = h.Call($"/containers/{syncName}/rename?name={syncAside}", "POST").Status;
            Log($"production's sync renamed to {syncAside} ->", st, Json.Str(tc.Inspect(syncAside), "State", "Status"));
        }
    }

    void Stage()
    {
        h.NeedLock();
        var (game, bridge) = (Game(), BridgeInspect());
        var dir = Dir(bridge);
        var record = ServerShip.Built();   // what server build built, held to its sources
        var pluginOut = Plugin.OutDir;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var program in ServerShip.Programs.Keys)
            files[$"overstrike2/{program}"] = File.ReadAllBytes(Path.Combine(ServerShip.OutDir, program, program));
        files["overstrike2/pre.sh"] = File.ReadAllBytes(Repo.P(ServerShip.Launcher.Split('/')));
        foreach (var cfg in ServerShip.Cfgs)
            files[$"cfg/{Path.GetFileName(cfg)}"] = File.ReadAllBytes(Repo.P(cfg.Split('/')));
        foreach (var f in Plugin.ShipList())
            files[$"plugins/{Plugin.Name}/{f}"] = File.ReadAllBytes(Path.Combine(pluginOut, f));
        var sync = SyncInspect();
        var syncCopies = "";
        if (sync is not null)
        {
            var state = TestCopy.BindSource(sync, SettingPath(sync, "syncState", SyncStateAt));
            var work = TestCopy.BindSource(sync, SettingPath(sync, "syncWork", SyncWorkAt));
            if (state is null || work is null)
                throw Out.Stop("REFUSED: production's sync has no bind for its state or its work folder");
            syncCopies = $"mkdir -p /s/rehearsal /w/rehearsal; find /s/rehearsal /w/rehearsal -mindepth 1 -delete; "
                         + "for f in /s/*; do [ \"$(basename \"$f\")\" = rehearsal ] || cp -a \"$f\" /s/rehearsal/; done; "
                         + "for f in /w/*; do case \"$(basename \"$f\")\" in rehearsal|handover|farm) ;; *) cp -a \"$f\" /w/rehearsal/;; esac; done; "
                         + "chown --reference=/s /s/rehearsal; chown --reference=/w /w/rehearsal; ";
        }
        var plugins = TestCopy.BindSource(game, TestCopy.PluginsAtOf(game));
        var binds = new List<string> { $"{dir[..dir.LastIndexOf('/')]}:/r", $"{BridgeData(bridge)}:/b:ro" };
        if (plugins is not null)
            binds.Add($"{plugins}:/pl:ro");
        if (sync is not null)
        {
            binds.Add($"{TestCopy.BindSource(sync, SettingPath(sync, "syncState", SyncStateAt))}:/s");
            binds.Add($"{TestCopy.BindSource(sync, SettingPath(sync, "syncWork", SyncWorkAt))}:/w");
        }
        var name = dir[(dir.LastIndexOf('/') + 1)..];
        var (code, output) = StageHelper(binds, files, name, game,
            $"set -e; R=/r/{name}; mkdir -p $R/bridge; find $R/bridge -mindepth 1 -delete; cp -a /b/. $R/bridge/; rm -f $R/bridge/maintenance; "
            + (plugins is not null ? $"for f in /pl/*; do [ \"$(basename \"$f\")\" = {Plugin.Name} ] || cp -a \"$f\" $R/plugins/; done; " : "")
            + "chown -R --reference=/b $R/bridge; chown --reference=/r $R $R/overstrike2 $R/plugins $R/cfg; " + syncCopies
            + "echo bridge copy: $(find $R/bridge -type f | wc -l) files; ls -la $R/overstrike2 | cut -c1-90");
        Log("stage:", code, $"(programs built at {Json.Str(record, "commit")})");
        Out.Print(output);
        if (code != 0)
            throw Out.Stop("REFUSED: staging failed");
    }

    /// <summary>A helper that receives files as a tar into /r/&lt;name&gt; and runs a script after.</summary>
    (int Code, string Output) StageHelper(List<string> binds, Dictionary<string, byte[]> files, string name, JsonObject game, string script)
    {
        var labels = new JsonObject();
        foreach (var (k, v) in tc.ExtraLabels)
            labels[k] = v;
        labels[TestCopy.TestLabel] = "helper: rehearsal stage";
        var created = h.Call($"/containers/create?name={tc.Game}-rehearsal-stage", "POST", new JsonObject
        {
            ["Image"] = Json.Str(game, "Image"), ["User"] = "0:0", ["Entrypoint"] = new JsonArray("/bin/bash", "-c"), ["Cmd"] = new JsonArray("sleep 900"), ["Labels"] = labels,
            ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray(binds.Select(b => (JsonNode?)b).ToArray()), ["NetworkMode"] = "none", ["RestartPolicy"] = new JsonObject { ["Name"] = "no" } },
        });
        var cid = Json.Str(created.Json, "Id") ?? throw Out.Stop("REFUSED: the staging helper was not created");
        try
        {
            h.Call($"/containers/{cid}/start", "POST");
            h.ExecIn(cid, ["/bin/bash", "-c", $"mkdir -p /r/{name}"]);
            h.Call($"/containers/{cid}/archive?path=/r/{name}", "PUT", raw: Tar(files), ctype: "application/x-tar", timeout: 600);
            var output = h.ExecIn(cid, ["/bin/bash", "-c", "( " + script + " ) 2>&1; echo EXIT=$?"], timeout: 1800);
            var m = Regex.Match(output, @"EXIT=(\d+)\s*$");
            return (m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1, Py.Strip(Regex.Replace(output, @"EXIT=\d+\s*$", "")));
        }
        finally
        {
            h.Call($"/containers/{cid}?force=true", "DELETE");
        }
    }

    byte[] Tar(Dictionary<string, byte[]> files)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds((long)h.Now());
        var buf = new MemoryStream();
        using (var tar = new System.Formats.Tar.TarWriter(buf, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
        {
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (rel, data) in files.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var parts = rel.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    var d = string.Join('/', parts.Take(i));
                    if (dirs.Add(d))
                        tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, d) { Mode = (UnixFileMode)0b111_101_101, ModificationTime = now });
                }
                tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, rel)
                {
                    Mode = (UnixFileMode)0b110_100_100, ModificationTime = now, DataStream = new MemoryStream(data),
                });
            }
        }
        return buf.ToArray();
    }

    void Prep()
    {
        h.NeedLock();
        var d = tc.Inspect(tc.Game);
        if (d is null || TestCopy.IsTest(d))
            throw Out.Stop($"REFUSED: {tc.Game} is not production");
        Directory.CreateDirectory(tc.State);
        var i = tc.A2sNow();
        if (i is null || i.Humans > 0)
            throw Out.Stop($"REFUSED: A2S {(i is null ? "does not answer" : $"shows {i.Humans} human(s)")}");
        ServerShip.Built();   // what stage ships must be built before production goes aside
        if (!File.Exists(Path.Combine(Plugin.OutDir, Plugin.Name + ".dll")))
            throw Out.Stop("REFUSED: no plugin build - os2-deploy plugin build first");
        tc.CmdPrep();
        tc.CmdBridgePrep();
        SyncAside();
        Backup();
        Stage();
    }

    // ------------------------------------------------------------------------------------------------------- the compose file

    /// <summary>The candidate's compose text, made a rehearsal: production's folders replaced by the rehearsal's, a test secret in the place
    /// of the bridge's, the bridge on its tag, the test labels, production's network as an external one, and the settings with the
    /// rehearsal's values. (compose text, settings text); the settings text holds secrets, and neither is ever printed.</summary>
    public static (string Compose, string Settings) ComposeText(string text, Dictionary<string, string> replace, string secret, string network, string bridgeImage,
                                                               Dictionary<string, string> labels, string daily, int testPort, bool viaFile, string settingsFile)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw Out.Stop("REFUSED: the compose file is no single YAML mapping");
        var services = root.Children.TryGetValue(new YamlScalarNode("services"), out var s) && s is YamlMappingNode sm ? sm : throw Out.Stop("REFUSED: the compose file has no services");
        // the bridge's secret: the last part of the game server's log push address, wherever else it is written
        var oldSecret = services.Children.Values.OfType<YamlMappingNode>().Select(svc => EnvValue(svc, "CS2_LOG_HTTP_URL")).Where(v => v is not null)
                                .Select(v => Regex.Match(v!.Replace("\\/", "/", StringComparison.Ordinal), "/log/([A-Za-z0-9]+)$")).Where(m => m.Success).Select(m => m.Groups[1].Value).FirstOrDefault()
                        ?? throw Out.Stop("REFUSED: no CS2_LOG_HTTP_URL with a secret in the compose file - no test secret can take its place");
        stream = new YamlStream();
        stream.Load(new StringReader(text.Replace(oldSecret, secret, StringComparison.Ordinal)));
        root = (YamlMappingNode)stream.Documents[0].RootNode;
        services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];
        var settingsText = services.Children.Values.OfType<YamlMappingNode>().Select(svc => EnvValue(svc, Check.Content)).FirstOrDefault(v => v is not null)
                           ?? throw Out.Stop($"REFUSED: no service has {Check.Content} - the rehearsal's settings need a settings text");
        JsonObject settings;
        try
        {
            settings = Json.Jsonc(settingsText) as JsonObject ?? throw new JsonSyntaxError("no object");
        }
        catch (JsonSyntaxError)
        {
            throw Out.Stop("REFUSED: the compose file's settings text cannot be read");
        }
        JsonObject Section(JsonObject o, string name) => o[name] as JsonObject ?? (JsonObject)(o[name] = new JsonObject());
        Section(settings, "maps")["delete"] = "dryrun";
        Section(settings, "restarts")["daily"] = daily;
        Section(settings, "hud")["itemPage"] = false;
        var sync = Section(settings, "sync");
        sync["publish"] = false;
        sync["handover"] = false;
        sync["testPort"] = testPort;
        Section(sync, "mail")["smtpHost"] = "";
        var paths = Section(settings, "paths");
        paths["syncState"] = (Json.AsStr(paths["syncState"]) is { Length: > 0 } st && st.StartsWith('/') ? st.TrimEnd('/') : SyncStateAt) + "/rehearsal";
        paths["syncWork"] = (Json.AsStr(paths["syncWork"]) is { Length: > 0 } wk && wk.StartsWith('/') ? wk.TrimEnd('/') : SyncWorkAt) + "/rehearsal";
        var settingsOut = Json.Dumps(settings, indent: 1);

        foreach (var key in root.Children.Keys.OfType<YamlScalarNode>().Where(k => k.Value is { } v && (v == "configs" || v.StartsWith("x-", StringComparison.Ordinal))).ToList())
            root.Children.Remove(key);
        var networks = root.Children.TryGetValue(new YamlScalarNode("networks"), out var n) && n is YamlMappingNode nm ? nm : (YamlMappingNode)(root.Children[new YamlScalarNode("networks")] = new YamlMappingNode());
        networks.Children[new YamlScalarNode("default")] = new YamlMappingNode(new YamlScalarNode("name"), new YamlScalarNode(network),
                                                                                new YamlScalarNode("external"), new YamlScalarNode("true") { Style = YamlDotNet.Core.ScalarStyle.Plain });
        foreach (var (key, value) in services.Children)
        {
            if (value is not YamlMappingNode svc)
                continue;
            svc.Children.Remove(new YamlScalarNode("configs"));
            var svcLabels = svc.Children.TryGetValue(new YamlScalarNode("labels"), out var l) && l is YamlMappingNode lm ? lm : (YamlMappingNode)(svc.Children[new YamlScalarNode("labels")] = new YamlMappingNode());
            foreach (var (k, v) in labels)
                svcLabels.Children[new YamlScalarNode(k)] = new YamlScalarNode(v) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted };
            svcLabels.Children[new YamlScalarNode(TestCopy.TestLabel)] = new YamlScalarNode(Purpose) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted };
            var volumes = svc.Children.TryGetValue(new YamlScalarNode("volumes"), out var vol) && vol is YamlSequenceNode vs ? vs : null;
            var newVolumes = new YamlSequenceNode();
            foreach (var v in volumes?.Children ?? [])
            {
                if (v is YamlScalarNode { Value: { } spec })
                {
                    var colon = spec.IndexOf(':');
                    var src = colon < 0 ? spec : spec[..colon];
                    newVolumes.Add(new YamlScalarNode(replace.TryGetValue(src, out var to) ? to + spec[src.Length..] : spec));
                }
                else
                    newVolumes.Add(v);
            }
            var env = svc.Children.TryGetValue(new YamlScalarNode("environment"), out var e) && e is YamlMappingNode em ? em : null;
            if (env is not null && env.Children.ContainsKey(new YamlScalarNode(Check.Content)))
            {
                if (viaFile)
                {
                    env.Children.Remove(new YamlScalarNode(Check.Content));
                    newVolumes.Add(new YamlScalarNode($"{settingsFile}:/etc/overstrike2/overstrike2.jsonc:ro"));
                }
                else
                    env.Children[new YamlScalarNode(Check.Content)] = new YamlScalarNode(settingsOut) { Style = YamlDotNet.Core.ScalarStyle.Literal };
            }
            if (volumes is not null || newVolumes.Children.Count > 0)
                svc.Children[new YamlScalarNode("volumes")] = newVolumes;
            if (key is YamlScalarNode { Value: "bridge" })
            {
                svc.Children[new YamlScalarNode("image")] = new YamlScalarNode(bridgeImage);
                svc.Children[new YamlScalarNode("read_only")] = new YamlScalarNode("true") { Style = YamlDotNet.Core.ScalarStyle.Plain };
            }
        }
        var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return (writer.ToString(), settingsOut);
    }

    static string? EnvValue(YamlMappingNode svc, string name)
    {
        if (!svc.Children.TryGetValue(new YamlScalarNode("environment"), out var e))
            return null;
        if (e is YamlMappingNode m)
            return m.Children.TryGetValue(new YamlScalarNode(name), out var v) && v is YamlScalarNode sv ? sv.Value : null;
        if (e is YamlSequenceNode seq)
            foreach (var item in seq.Children.OfType<YamlScalarNode>())
                if (item.Value is { } t && t.StartsWith(name + "=", StringComparison.Ordinal))
                    return t[(name.Length + 1)..];
        return null;
    }

    // ------------------------------------------------------------------------------------------------------ the stack manager

    /// <summary>The stack manager that creates the throwaway stack from a compose text (rehearse.manager, its API's base, and rehearse.endpoint,
    /// the Docker environment it runs the stack on): POST &lt;manager&gt;/stacks/create/standalone/string?endpointId=&lt;endpoint&gt;, GET
    /// &lt;manager&gt;/stacks, DELETE &lt;manager&gt;/stacks/&lt;id&gt;?endpointId=&lt;endpoint&gt;, with the docker.headers.</summary>
    (string Base, string Endpoint) Manager()
    {
        var b = h.Cfg.Str("rehearse.manager").TrimEnd('/');
        var id = h.Cfg.Str("rehearse.endpoint");
        if (b.Length == 0 || id.Length == 0)
            throw Out.Stop("REFUSED: no rehearse.manager and rehearse.endpoint in the private config - nothing to create the stack with");
        return (b, id);
    }

    public Func<string, string, JsonNode?, (int Status, JsonNode? Body)> ManagerCall;

    (int, JsonNode?) RealManagerCall(string method, string path, JsonNode? body)
    {
        var (b, _) = Manager();
        var docker = new Docker(h.Cfg);
        using var http = Docker.Client(docker.TlsVerify);
        using var req = new HttpRequestMessage(new HttpMethod(method), b + path);
        foreach (var (k, v) in docker.Headers)
            req.Headers.TryAddWithoutValidation(k, v);
        if (body is not null)
            req.Content = new StringContent(Json.Dumps(body), Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var resp = http.Send(req, cts.Token);
        var bytes = resp.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
        JsonNode? parsed = null;
        if (bytes.Length > 0 && bytes[0] is (byte)'{' or (byte)'[')
            parsed = Json.Parse(bytes);
        return ((int)resp.StatusCode, parsed ?? JsonValue.Create(Encoding.UTF8.GetString(bytes)));
    }

    string StackName => h.Cfg.Str("rehearse.stack") is { Length: > 0 } s ? s : tc.Game + "-rehearsal";

    /// <summary>Production's game server and bridge as the rehearsal reads them: the inspects saved at prep while they are aside, else the
    /// running containers (only read; a test copy or a candidate is refused).</summary>
    (JsonObject Game, JsonObject Bridge) Production()
    {
        if (File.Exists(tc.ProdSaved) && File.Exists(tc.BridgeSaved) && tc.Inspect(tc.Aside) is not null)
            return (Game(), BridgeInspect());
        var (game, bridge) = (tc.Inspect(tc.Game), tc.Inspect(tc.Bridge));
        if (game is null || bridge is null || TestCopy.IsTest(game) || TestCopy.IsTest(bridge))
            throw Out.Stop("REFUSED: production is neither aside (with the inspects prep saved) nor running under its own names");
        return (game, bridge);
    }

    /// <summary>What up creates: (compose text, settings text, the rehearsal's folder). Both texts hold secrets and are never printed.</summary>
    (string Compose, string Settings, string Dir) Build(JsonObject game, JsonObject bridge, string daily, string via, string tag, string? composePath)
    {
        var path = composePath ?? Path.Combine([h.Cfg.Str("stack.repo", required: true), .. h.Cfg.Str("stack.file", required: true).Replace('\\', '/').Split('/')]);
        if (!File.Exists(secretFile))
        {
            Directory.CreateDirectory(tc.State);
            File.WriteAllText(secretFile, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        }
        var secret = File.ReadAllText(secretFile).Trim();
        var network = Json.Obj(game, "NetworkSettings", "Networks").Select(kv => kv.Key).FirstOrDefault() ?? throw Out.Stop("REFUSED: production's game server is on no network");
        var dir = Dir(bridge);
        var (compose, settings) = ComposeText(File.ReadAllText(path, Encoding.UTF8), Replacements(game, bridge), secret, network, $"{tc.BridgeRepo}:{tag}",
                                              new Dictionary<string, string>(tc.ExtraLabels), daily, ConfigExtensions.Int(h.Cfg.Get("rehearse.test_port", 27216L), 27216),
                                              via == "file", dir + "/settings/overstrike2.jsonc");
        return (compose, settings, dir);
    }

    /// <summary>A dry run of up: the compose file it would create, written to the harness's state folder, and what it changes, named.</summary>
    void ComposeDry(string daily, string via, string tag, string? composePath)
    {
        var (game, bridge) = Production();
        var (compose, settings, dir) = Build(game, bridge, daily, via, tag, composePath);
        var file = Path.Combine(tc.State, "rehearsal-compose.yml");
        File.WriteAllText(file, compose);
        Log($"the compose file up would create: {file} (secrets inside: never printed)");
        foreach (var (from, to) in Replacements(game, bridge))
            Log($"  {from} -> {to}");
        var s = Json.Parse(settings);
        Log($"  settings: maps.delete {Json.Str(s, "maps", "delete")}, restarts.daily {Json.Str(s, "restarts", "daily")}, hud.itemPage {Json.PyStr(Json.At(s, "hud", "itemPage"))}, "
            + $"sync.publish {Json.PyStr(Json.At(s, "sync", "publish"))}, sync.handover {Json.PyStr(Json.At(s, "sync", "handover"))}, sync.testPort {Json.PyStr(Json.At(s, "sync", "testPort"))}, "
            + $"sync.mail.smtpHost {Py.Repr(Json.Str(s, "sync", "mail", "smtpHost"))}, paths.syncState {Json.Str(s, "paths", "syncState")}, paths.syncWork {Json.Str(s, "paths", "syncWork")}");
        Log($"  the stack {StackName} on {tc.BridgeRepo}:{tag}, in {dir}");
    }

    void Up(string daily, string via, string tag, string? composePath)
    {
        h.NeedLock();
        if (tc.Inspect(tc.Aside) is null || tc.Inspect(tc.BridgeAside) is null)
            throw Out.Stop("REFUSED: production is not aside - prep first");
        var (game, bridge) = (Game(), BridgeInspect());
        var (compose, settings, dir) = Build(game, bridge, daily, via, tag, composePath);
        if (via == "file")
        {
            var (code, output) = StageHelper([$"{dir[..dir.LastIndexOf('/')]}:/r"], new() { ["settings/overstrike2.jsonc"] = Encoding.UTF8.GetBytes(settings) },
                                             dir[(dir.LastIndexOf('/') + 1)..], game, $"chown -R --reference=/r /r/{dir[(dir.LastIndexOf('/') + 1)..]}/settings; echo settings file written");
            Log("settings file:", code, output.Replace("\n", " | "));
        }
        File.WriteAllText(Path.Combine(tc.State, "rehearsal-compose.yml"), compose);   // secrets inside: the harness's state folder, never printed
        var t0 = h.Now();
        var (_, endpoint) = Manager();
        var (status, made) = ManagerCall("POST", $"/stacks/create/standalone/string?endpointId={endpoint}",
                                         new JsonObject { ["name"] = StackName, ["stackFileContent"] = compose, ["env"] = new JsonArray() });
        Log($"stack create -> {status} after {Fmt.Fixed(h.Now() - t0, 0)} s", status is 200 or 201 ? "" : Py.Slice(Json.PyStr(made), 0, 1500));
        foreach (var name in Names.Values.Where(x => x.Length > 0))
        {
            var d = tc.Inspect(name);
            Log(name, "->", d is null ? "None" : $"{Json.Str(d, "State", "Status")} {Py.Slice(Json.Str(d, "Image"), 7, 19)} {Py.Repr(TestCopy.Pinned(d))}");
        }
        var (i, waited) = tc.WaitA2s(1, 480);
        Log($"A2S after {Fmt.Fixed(waited, 0)} s more:", Py.Repr(i));
    }

    void Down()
    {
        h.NeedLock();
        var (_, endpoint) = Manager();
        var (status, stacks) = ManagerCall("GET", "/stacks", null);
        var id = (stacks as JsonArray ?? []).OfType<JsonObject>().Where(x => Json.Str(x, "Name") == StackName).Select(x => Json.PyStr(x["Id"])).FirstOrDefault();
        if (id is null)
            Log("no stack", StackName, status == 200 ? "" : $"(stacks: HTTP {status})");
        else
        {
            var t0 = h.Now();
            var (st, output) = ManagerCall("DELETE", $"/stacks/{id}?endpointId={endpoint}", null);
            Log($"stack delete -> {st} after {Fmt.Fixed(h.Now() - t0, 0)} s", Py.Slice(Json.PyStr(output), 0, 200));
        }
        foreach (var name in Names.Values.Where(x => x.Length > 0))
        {
            var d = tc.Inspect(name);
            if (d is null)
                continue;
            if (!TestCopy.IsTest(d))
                throw Out.Stop($"REFUSED: {name} exists and is not a rehearsal container - look");
            Log(name, "left behind; removing ->", h.Call($"/containers/{name}?force=true", "DELETE").Status);
        }
    }

    void Restore()
    {
        h.NeedLock();
        if (Names.Values.Where(x => x.Length > 0).Any(n => tc.Inspect(n) is not null))
            throw Out.Stop("REFUSED: a stack container exists - down first");
        var game = Game();
        var gameTree = GameTree(game);
        var (code, output) = tc.Helper($"{tc.Game}-rehearsal-restore", [$"{gameTree}:/g"],
            "set -e; U=/g/update-watch; B=$U/rehearsal-backup; for f in state.json keep.json installed.json; do if [ -f $B/$f ]; then cp -p $B/$f $U/$f; else rm -f $U/$f; fi; done; "
            + "for f in $U/deleted-*.tsv; do [ -e \"$f\" ] || continue; n=$(basename \"$f\"); grep -q \" $n$\" $B/listing.txt || { echo removing $n; rm -f \"$f\"; }; done; "
            + "for s in $(ls $U/steamlogs 2>/dev/null); do grep -qx \"$s\" $B/steamlogs.txt || { echo removing steamlogs/$s; rm -rf \"$U/steamlogs/$s\"; }; done; "
            + "sha256sum $U/state.json $U/keep.json 2>/dev/null | cut -c1-16,65-");
        Log("records restored:", code);
        Out.Print(output);
        if (code != 0)
            throw Out.Stop("REFUSED: the records were not restored - look before production starts");
        tc.CmdBridgeRestore();
        tc.CmdRestore();
        if (syncName.Length > 0 && tc.Inspect(syncAside) is not null)
        {
            var st = h.Call($"/containers/{syncAside}/rename?name={syncName}", "POST").Status;
            var st2 = h.Call($"/containers/{syncName}/start", "POST").Status;
            Log("production's sync back ->", st, st2, Json.Str(tc.Inspect(syncName), "State", "Status"));
            File.Delete(SyncSaved);
        }
    }

    /// <summary>The rehearsal's folder, the records' backup and the sync's two copies gone, with the stack down: after restore (which deletes
    /// the saved inspects) where things are comes from production's own containers, before it from the saved inspects.</summary>
    public void Clean()
    {
        h.NeedLock();
        foreach (var name in Names.Values.Where(n => n.Length > 0))
            if (tc.Inspect(name) is { } d && TestCopy.IsTest(d))
                throw Out.Stop($"REFUSED: {name} is the rehearsal's - down first: the folders are its mounts");
        var game = Production(tc.ProdSaved, tc.Game, "production's game server inspect");
        var bridge = Production(tc.BridgeSaved, tc.Bridge, "production's bridge inspect");
        var dir = Dir(bridge);
        var binds = new List<string> { $"{dir[..dir.LastIndexOf('/')]}:/p", $"{GameTree(game)}:/g" };
        var script = $"rm -rf -- '/p/{dir[(dir.LastIndexOf('/') + 1)..]}' /g/update-watch/rehearsal-backup; ";
        var sync = syncName.Length > 0 ? Production(SyncSaved, syncName, "production's sync inspect") : null;
        if (sync is not null)
        {
            binds.Add($"{Need(TestCopy.BindSource(sync, SettingPath(sync, "syncState", SyncStateAt)), "the sync's state")}:/s");
            binds.Add($"{Need(TestCopy.BindSource(sync, SettingPath(sync, "syncWork", SyncWorkAt)), "the sync's work folder")}:/w");
            script += "rm -rf /s/rehearsal /w/rehearsal; ";
        }
        var (code, output) = tc.Helper($"{tc.Game}-rehearsal-clean", binds, script + "echo cleaned", image: Json.Str(game, "Image"));
        Log("clean:", code, Py.Slice(output.Replace("\n", " "), 0, 400));
        File.Delete(secretFile);
    }

    void Logs(string which, string seconds, string? rx)
    {
        var since = (long)(h.Now() - double.Parse(seconds, CultureInfo.InvariantCulture));
        var raw = h.Call($"/containers/{Names[which]}/logs?stdout=1&stderr=1&since={since}").Body;
        var secret = File.Exists(secretFile) ? File.ReadAllText(secretFile).Trim() : "\0";
        foreach (var line in Py.SplitLines(Harness.Demux(raw)))
            if (rx is null || Regex.IsMatch(line, rx))
                Out.Print(Py.Slice(line.Replace(secret, "<secret>", StringComparison.Ordinal), 0, 400));
    }

    void Sh(string which, string cmd, string? user)
    {
        var body = new JsonObject { ["AttachStdout"] = true, ["AttachStderr"] = true, ["Cmd"] = new JsonArray("/bin/bash", "-c", cmd) };
        if (user is not null)
            body["User"] = user;
        var created = h.Call($"/containers/{Names[which]}/exec", "POST", body);
        var started = h.Call($"/exec/{Json.Str(created.Json, "Id")}/start", "POST", new JsonObject { ["Detach"] = false, ["Tty"] = false }, timeout: 600);
        Out.Print(Harness.Demux(started.Body));
    }

    /// <summary>One new file into the rehearsal's program folder, as a deploy would: written beside and renamed into place.</summary>
    void Ship(string path, string? name)
    {
        h.NeedLock();
        name ??= Path.GetFileName(path);
        var (game, bridge) = (Game(), BridgeInspect());
        var dir = Dir(bridge);
        var folder = dir[(dir.LastIndexOf('/') + 1)..];
        var (code, output) = StageHelper([$"{dir[..dir.LastIndexOf('/')]}:/r"], new() { [$".incoming/{name}"] = File.ReadAllBytes(path) }, folder, game,
                                         $"mv -f /r/{folder}/.incoming/{name} /r/{folder}/overstrike2/{name} && rmdir /r/{folder}/.incoming; ls -la /r/{folder}/overstrike2 | cut -c1-80");
        Log("ship:", code, Py.Slice(output.Replace("\n", " | "), 0, 400));
    }

    void Status()
    {
        tc.CmdStatus();
        if (syncName.Length > 0)
            Out.Print($"{syncName}: {Json.Str(tc.Inspect(syncName), "State", "Status") ?? "None"} | aside: {Json.Str(tc.Inspect(syncAside), "State", "Status") ?? "None"}");
        try
        {
            var (_, stacks) = ManagerCall("GET", "/stacks", null);
            Out.Print("stack: " + Py.Repr((stacks as JsonArray ?? []).OfType<JsonObject>().Select(x => Json.Str(x, "Name")).Where(n => n == StackName).ToList()));
        }
        catch (Exit e)
        {
            Out.Print("stack: unknown - " + e.Message);
        }
    }

    public const string Doc = "rehearse prep | backup | stage | up [--daily HH:MM] [--via env|file] [--tag T] [--compose FILE] | compose (the same options) | down | restore | clean | "
                              + "logs <cs2|bridge|sync> <seconds> [regex] | sh <cs2|bridge|sync> \"<cmd>\" [user] | rcon \"<cmd>\"... | ship <file> [name] | status";

    public static int Command(string[] argv)
    {
        var (args, config) = Harness.TakeConfig(argv);
        if (args.Count == 0)
        {
            Out.Print("usage: os2-deploy " + Doc);
            return 0;
        }
        var r = new Rehearse(new TestCopy(new Harness(config)));
        string? Opt(string name, string? dflt)
        {
            var i = args.IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : dflt;
        }
        string Role(int i) => i < args.Count && r.Names.ContainsKey(args[i]) && r.Names[args[i]].Length > 0 ? args[i]
                              : throw Out.Stop("usage: os2-deploy " + Doc);
        switch (args[0])
        {
            case "prep": r.Prep(); break;
            case "backup": r.Backup(); break;
            case "stage": r.Stage(); break;
            case "up":
            case "compose":
                var via = Opt("--via", "env")!;
                if (via is not ("env" or "file"))
                    throw Out.Stop("usage: --via env|file");
                if (args[0] == "up")
                    r.Up(Opt("--daily", "off")!, via, Opt("--tag", "candidate")!, Opt("--compose", null));
                else
                    r.ComposeDry(Opt("--daily", "off")!, via, Opt("--tag", "candidate")!, Opt("--compose", null));
                break;
            case "down": r.Down(); break;
            case "restore": r.Restore(); break;
            case "clean": r.Clean(); break;
            case "logs": r.Logs(Role(1), args.Count > 2 ? args[2] : throw Out.Stop("usage: logs <role> <seconds> [regex]"), args.Count > 3 ? args[3] : null); break;
            case "sh": r.Sh(Role(1), args.Count > 2 ? args[2] : throw Out.Stop("usage: sh <role> \"<cmd>\" [user]"), args.Count > 3 ? args[3] : null); break;
            case "rcon": Out.Print(r.h.Rcon([.. args.Skip(1)])); break;
            case "ship": r.Ship(args.Count > 1 ? args[1] : throw Out.Stop("usage: ship <file> [name]"), args.Count > 2 ? args[2] : null); break;
            case "status": r.Status(); break;
            default: throw Out.Stop($"unknown verb {args[0]}\nusage: os2-deploy {Doc}");
        }
        return 0;
    }
}
