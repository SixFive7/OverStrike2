// The bridge's HTTP side: the game server's log push, the plugin's polls and clicks, the restart watcher's and the sync's reports, and /health.
// Every request's work is handed to the event loop (Post, OnLoop), which owns all state.
using System.Net;
using System.Text;

static partial class Program
{
    // ---- inbound: the CS2 server POSTs its log lines to /log/<LOG_SECRET> ----
    static void StartHttp()
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(OperatingSystem.IsWindows() ? $"http://127.0.0.1:{HttpPort}/" : $"http://*:{HttpPort}/"); // wildcard needs admin rights on Windows
        listener.Start();
        new Thread(() => { while (true) try { Handle(listener.GetContext()); } catch (Exception e) { Log("http: " + e.Message); } }) { IsBackground = true }.Start();
    }

    static void Handle(HttpListenerContext c)
    {
        var path = c.Request.Url?.AbsolutePath;
        if (c.Request.HttpMethod == "GET" && path == Hud.StatePath + LogSecret) // the HUD plugin: a long poll with its version (HudTransport.cs)
        {
            HudPolled(Draws(c.Request.Url?.Query));
            if (HudVersionAsked(c.Request.Url?.Query) is { } have) HudLongPoll(c, have);
            else Answer(c, StateAnswer()); // no version: the plugin tells of a hold beginning or ending (draw=0/1) and wants no wait; the state as it is now
            return;
        }
        if (c.Request.HttpMethod == "GET" && path == Hud.CatalogPath + LogSecret) { Reply(c, CatalogText); return; }        // the menu's map list, when its version moved
        if (c.Request.HttpMethod == "GET" && path == Hud.SearchPath + LogSecret) { Reply(c, SearchText); return; }          // the search index, when its version moved
        if (c.Request.HttpMethod == "GET" && path == Hud.DashPath + LogSecret) { Reply(c, DashText); return; }            // the Sync page's block, when its version moved (SyncDash.cs)
        if (c.Request.HttpMethod == "POST" && path == Hud.ActPath + LogSecret) { HudClick(c); return; }               // a click on the HUD
        // The other programs' words to the bridge, each address from core's BridgeWire.cs, as they send them:
        if (c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.Removed) + LogSecret) { RemovedReport(c); return; }      // the Workshop sync: what Steam says about the listed maps
        if (c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.Reload) + LogSecret) { ReloadRequest(c); return; }       // read the data files again now (Reload.cs)
        if (c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.Ready) + LogSecret) { ReadyReport(c); return; }          // the restart watcher: the queued maps on disk (Stock.cs)
        if (c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.Quitting) + LogSecret) { QuitNoticeRequest(c); return; } // the restart watcher, right before its planned quit (CrashWatch.cs)
        if (c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.RestartNotice) + LogSecret) { RestartNoticeRequest(c); return; } // the restart watcher: a restart coming, or not after all (Restarts.cs)
        if (c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.Updated) + LogSecret) { UpdatedRequest(c); return; }     // the restart watcher: the new engine is up, and what it is (Restarts.cs)
        if (c.Request.HttpMethod == "GET" && path == BridgeWire.Route(BridgeWire.Health) + LogSecret) { HealthRequest(c); return; }        // is it up, and hearing the server
        var ok = c.Request.HttpMethod == "POST" && path == BridgeWire.Route(BridgeWire.Log) + LogSecret;
        var body = ok ? new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd() : "";
        var token = c.Request.Headers["X-Server-Instance-Token"]; // constant across changelevel, new per server process (see README)
        var gameMap = c.Request.Headers["X-Game-Map"];
        c.Response.StatusCode = ok ? 200 : 404;
        c.Response.ContentLength64 = 0;
        c.Response.Close();
        if (ok) { Interlocked.Increment(ref logPosts); Interlocked.Exchange(ref lastLogTicks, DateTime.UtcNow.Ticks); }
        if (ok) Post(() =>
        {
            if (token != null) Session(token);
            foreach (var line in body.Split('\n')) Ingest(line.TrimEnd('\r'), gameMap);
        });
    }

    // ---- GET /health/<LOG_SECRET>: what a deploy (os2-deploy's bridge and check) and the data work need to see - up, hearing the server, what it loaded ----
    static long logPosts, lastLogTicks;                 // log pushes received (HTTP thread)
    static readonly DateTime StartedUtc = DateTime.UtcNow;

    static string HealthText()
    {
        var last = Interlocked.Read(ref lastLogTicks);
        return string.Join("\n",
            BridgeWire.HealthWord,
            $"started {StartedUtc:yyyy-MM-ddTHH:mm:ssZ}",
            $"log pushes {Interlocked.Read(ref logPosts)}" + (last == 0 ? ", none yet" : $", the last {(DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalSeconds:0} s ago"),
            $"maps {Maps.Count} + {Officials.Count} official; broken {Broken.Count}, non-maps {NonMaps.Count}, gone from the Workshop {Removed.Count}, blocked {Blocked.Count}",
            $"map {CurrentLabel()}",
            $"crash watch: spawn {(SpawnSeen?.ToString() ?? "not read yet")}, {S.Crashes.Count} map(s) with crashes",
            $"search: {Search.Count} maps indexed, version {Search.Version}",
            SelectionHealth(),
            NumbersHealth(),
            StockHealth(),
            ReasonHealth(),
            ModeHealth(),
            VoteHealth(),
            RestartHealth(),
            SyncHealth(),
            Files?.Health(DateTime.Now) ?? "log files: off (the console alone)",
            $"maintenance {(MaintenanceOn ? "on" : "off")}") + "\n";
    }

    static void HealthRequest(HttpListenerContext c)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => done.TrySetResult(HealthText())); // on the event loop, where the state lives
        Reply(c, done.Task.Wait(5000) ? done.Task.Result : "busy: the bridge did not get to it within 5 s");
    }

    /// <summary>A POST body handed to the event loop, where the state lives, and its answer sent back within 5 s.</summary>
    static void OnLoop(HttpListenerContext c, Func<string, string> apply, string what)
    {
        var body = new StreamReader(c.Request.InputStream, Encoding.UTF8).ReadToEnd();
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { done.TrySetResult(apply(body)); } catch (Exception e) { Log($"{what}: " + e); done.TrySetResult("error: " + e.Message); } });
        Reply(c, done.Task.Wait(5000) ? done.Task.Result : "busy: the bridge did not get to it within 5 s");
    }

    static void Reply(HttpListenerContext c, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        c.Response.StatusCode = 200;
        c.Response.ContentType = "text/plain; charset=utf-8";
        c.Response.ContentLength64 = bytes.Length;
        c.Response.OutputStream.Write(bytes);
        c.Response.Close();
    }
}
