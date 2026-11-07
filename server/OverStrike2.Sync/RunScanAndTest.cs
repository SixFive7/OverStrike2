// A run's tests: the scan of every staged download, then the load tests of the maps on the test engine, to a verdict.
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    /// <summary>Scan every staged item that has no verdict, then load the maps among them on the test engine. Returns the items this batch
    /// decided.</summary>
    public List<Verdicts.Outcome> ScanAndTest()
    {
        var t = Section("tests", () => new JsonObject { ["rows"] = new JsonArray() });
        var staged = Directory.Exists(Content) ? Directory.EnumerateFileSystemEntries(Content).Select(p => Path.GetFileName(p)!).OrderBy(x => x, StringComparer.Ordinal).ToList() : [];
        var only = Cfg.OnlyIds;                            // a manual run limited to some maps tests only those
        var work = new List<string>();
        foreach (var wid in staged)
        {
            if (only is not null && !only.Contains(wid))
                continue;
            var rec = State.Item(wid);
            if (rec is null || Node.Str(rec["status"]) is not ("downloaded" or "tested"))
                continue;
            if (!rec.ContainsKey("scan"))
            {
                try
                {
                    rec["scan"] = Seams.ScanItem(Path.Combine(Content, wid));
                }
                catch (Exception e) when (e is not StopRequested)   // a malformed upload must not end the run
                {
                    rec["scan"] = new JsonObject { ["verdict"] = "unknown", ["names"] = new JsonArray(), ["error"] = Text.Cut(Text.Of(e), 200) };
                }
            }
            work.Add(wid);
        }
        State.Save();
        var outcomes = new List<Verdicts.Outcome>();
        var toLoad = new List<string>();
        foreach (var wid in work)
        {
            var rec = State.Item(wid)!;
            var (kind, detail) = Verdicts.Final(rec, Day0);
            if (kind == "pending")
                toLoad.Add(wid);
            else
                outcomes.Add(new Verdicts.Outcome(wid, kind, detail, rec));
        }
        if (toLoad.Count > 0)
        {
            var version = Seams.PatchVersion(Cfg.GameDir);
            if (!WaitForNobody("tests", 600))
                new Stop(Stop.Routine, "somebody was on the server until the deadline").Into(t);
            else
            {
                var stale = Seams.KillStaleEngines(Path.Combine(Cfg.WorkDir, "farm"), Clock);
                if (stale > 0)
                    L($"killed {stale} test engine(s) left running from an earlier run");
                try
                {
                    L(Seams.BuildTestTree(Cfg.GameDir, Cfg.WorkDir, Cfg.CfgDir, Clock));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    new Stop(Stop.TestTree, $"the test tree could not be built: {e.Message}").Into(t);
                    toLoad = [];
                }
                Engine = Seams.TestEngine(Cfg, Cfg.WorkDir, Cfg.Home, Clock, L);
                Engine.Starts = EngineStarts;
                var budget = new DownloadBudget(State, Day0);
                var needStart = true;
                try
                {
                    for (var step = 0; step < toLoad.Count; step++)
                    {
                        var wid = toLoad[step];
                        var rec = State.Item(wid)!;
                        OnMap(wid, step, toLoad.Count);
                        while (true)
                        {
                            var (kind, _) = Verdicts.Final(rec, Day0);
                            if (kind != "pending")
                                break;
                            if (Left() < 300)
                            {
                                new Stop(Stop.Routine, "the run's deadline").Into(t);
                                break;
                            }
                            if (Seams.PatchVersion(Cfg.GameDir) != version)
                            {
                                new Stop(Stop.Routine, $"the game was updated during the tests (steam.inf {version ?? "None"} -> {Seams.PatchVersion(Cfg.GameDir) ?? "None"})").Into(t);
                                break;
                            }
                            if (!WaitForNobody("tests", 300))
                            {
                                new Stop(Stop.Routine, "somebody was on the server until the deadline").Into(t);
                                break;
                            }
                            if (needStart || !Engine.Alive())
                            {
                                var (ok, why) = Engine.Start();
                                L(why);
                                if (!ok)
                                {
                                    new Stop(Stop.TestEngine, why).Into(t);
                                    break;
                                }
                                needStart = false;
                            }
                            var levels = Node.StrList(Node.Get(rec["scan"], "maps"));
                            var load = Seams.TestLoad(Engine, wid, levels, Node.StrList(rec["tags"]), Cfg.Hold, () => (Humans() ?? 0) > 0, Clock);
                            List(rec, "loads").Add(load);
                            rec["tests"] = Node.Int(rec["tests"]) + 1;
                            rec["status"] = "tested";
                            if (Node.Truthy(load["engine_download"]))
                            {
                                budget.SpendBytes(Node.Int(load["engine_download"]));
                                try                         // what the engine loaded is its own copy: scan that one
                                {
                                    rec["scan"] = Seams.ScanItem(Path.Combine(Content, wid));
                                }
                                catch (Exception e) when (e is not StopRequested)
                                {
                                    if (rec["scan"] is JsonObject scan)
                                        scan["rescan_error"] = Text.Cut(Text.Of(e), 200);
                                }
                                L($"{wid}: the test engine fetched its own copy ({Text.Mb(Node.Int(load["engine_download"]))}) - scanned again");
                            }
                            State.Save();
                            L($"{wid} load {Node.Arr(rec["loads"]).Count}: {Node.Str(load["verdict"])} - {Node.Str(load["reason"])}");
                            if (Node.Str(load["verdict"]) is "errormap" or "crash" or "freeze")
                                needStart = true;          // a fresh engine: the old one is down or stuck on the error map
                            if (Node.Str(load["verdict"]) == "abort")
                            {
                                Engine.Stop();             // somebody joined: nothing of ours runs beside them
                                needStart = true;
                                break;
                            }
                        }
                        if (Node.Truthy(t["stop"]))
                            break;
                        var (k2, d2) = Verdicts.Final(rec, Day0);
                        if (k2 != "pending")
                            outcomes.Add(new Verdicts.Outcome(wid, k2, d2, rec));
                    }
                }
                finally
                {
                    Engine.Stop();
                    EngineStarts = Engine.Starts;
                }
            }
        }
        var rows = List(t, "rows");
        foreach (var o in outcomes)
        {
            var (wid, kind, detail, rec) = (o.Id, o.Kind, o.Detail, o.Rec);
            rec["verdict"] = kind;
            rec["decided"] = Day0;
            var kept = new JsonObject();
            foreach (var (k, v) in detail)
                if (k is "kind" or "detail" or "evidence" or "reason" or "level" or "mode" or "crashes" or "sample")
                    kept[k] = v?.DeepClone();
            rec["detail"] = kept;
            var title = Text.Cut(Node.StrOr(rec["title"]), 48);
            if (kind == "pass")
                rows.Add($"PASS     {title} ({wid}) - {Node.Str(detail["mode"])}, loads as {Node.Str(detail["level"])} in {Node.Str(detail["load_s"])}s");
            else if (kind == "nonmap")
                rows.Add($"NON-MAP  {title} ({wid}) - {Text.Cut(detail.ContainsKey("detail") ? Node.Str(detail["detail"]) : "", 110)}");
            else if (kind == "advert")
                rows.Add($"ADVERT   {title} ({wid}) - {Node.Str(detail["reason"])}: {Text.Cut(Node.StrOr(detail["sample"]), 80)}");
            else
                rows.Add($"{kind.ToUpperInvariant().PadRight(8)} {title} ({wid}) - {Text.Cut(detail.ContainsKey("evidence") ? Node.Str(detail["evidence"]) : "", 110)}");
        }
        var pending = work.Where(w => State.Item(w)?["verdict"] is null).ToList();
        if (pending.Count > 0)
        {
            L($"waiting for the next batch or run: {pending.Count} staged item(s) without a verdict");
            t["waiting_verdict"] = pending.Count;
        }
        Limits();
        State.Save();
        return outcomes;
    }
}
