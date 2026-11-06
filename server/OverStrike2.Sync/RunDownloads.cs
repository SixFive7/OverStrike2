// A run's downloads: one batch of the queue on one anonymous SteamCMD login.
using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

public sealed partial class SyncRun
{
    /// <summary>One batch: at most sync.batch downloads on one anonymous SteamCMD login. Returns how many it downloaded.</summary>
    public int Download(IReadOnlyDictionary<string, string> known, IReadOnlyDictionary<string, JsonObject> details)
    {
        var budget = new DownloadBudget(State, Day0);
        var dl = Section("downloads", () => new JsonObject { ["ok"] = 0, ["bytes"] = 0, ["failed"] = new JsonArray(), ["braked"] = new JsonArray() });
        try
        {
            Handover.TidyStaging(Staging, Cfg.HandoverDir, State.Items, L);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            L($"staging: not tidied ({e.Message})");
        }
        var stalled = new HashSet<string>(Stalls.Select(s => s.Id));          // a map that stalled in this run waits for the next one
        var todo = Queue(known, details).Where(w => !Staged(w) && !stalled.Contains(w)).ToList();
        if (todo.Count == 0)
        {
            dl["queued_left"] = 0;
            Limits(budget);
            return 0;
        }
        var (allowed, why) = budget.Allows();
        if (!allowed)
        {
            dl["stop"] = why;
            dl["queued_left"] = todo.Count;
            Limits(budget);
            return 0;
        }
        var steam = Seams.SteamCmd(Cfg, Staging, Path.Combine(Cfg.WorkDir, "steamcmd.log"), Clock);
        var started = false;
        int row = 0, denied = 0, got = 0;
        var done = new HashSet<string>();
        try
        {
            foreach (var wid in todo)
            {
                if (got >= Cfg.Batch)
                    break;
                OnMap(wid, got, Math.Min(todo.Count, Cfg.Batch));
                var rec = State.Item(wid)!;
                (allowed, why) = budget.Allows(wid);
                if (!allowed)
                {
                    if (why.Contains("no more downloads", StringComparison.Ordinal))
                    {
                        dl["stop"] = why;
                        break;
                    }
                    List(dl, "braked").Add(wid);           // downloaded twice today already: a bug somewhere, not again today
                    continue;
                }
                if (Left() < 600 + 25 * (got + 1))
                {
                    dl["stop"] = "the run's deadline (time is kept for this batch's tests)";
                    break;
                }
                var tWait = Clock.Now();
                if (!WaitForNobody("downloads", 600))
                {
                    dl["stop"] = "somebody was on the server until the deadline";
                    break;
                }
                if (started && Clock.Now() - tWait > 300)
                {
                    steam.Stop();                          // a long wait: a fresh login rather than a session gone stale
                    started = false;
                }
                if (!started)
                {
                    (started, why) = steam.Start();
                    if (!started)
                    {
                        dl["stop"] = $"SteamCMD did not log in anonymously: {why}";
                        Errors.Add(Node.Str(dl["stop"]));
                        if (SteamCmdWords.StopRx().IsMatch(why))
                            budget.RefuseForToday($"SteamCMD answered '{why}'");
                        break;
                    }
                    L($"SteamCMD: {why}");
                }
                var t = Clock.Now();
                var (good, reason, nbytes) = steam.Download(wid);
                if (good && Staged(wid))
                {
                    rec["attempts"] = 0;                   // the tries of a content version: this one is here
                    var onDisk = Handover.FolderBytes(Path.Combine(Content, wid));
                    var size = Math.Max(nbytes, onDisk);
                    budget.Spend(size, wid);
                    Bump(dl, "ok", 1);
                    got++;
                    Bump(dl, "bytes", size);
                    rec["status"] = "downloaded";
                    rec["downloaded"] = Day0;
                    rec["loads"] = Node.Str(rec["why"]) == "changed" ? new JsonArray() : Node.Truthy(rec["loads"]) ? rec["loads"]!.DeepClone() : new JsonArray();
                    foreach (var stale in new[] { "scan", "verdict", "decided", "detail", "evidence" })
                        rec.Remove(stale);
                    (row, denied) = (0, 0);
                    L($"downloaded {wid} {Text.Cut(Node.StrOr(rec["title"]), 40)} ({Text.Mb(size)}) in {Text.Fixed(Clock.Now() - t, 0)} s");
                }
                else
                {
                    List(dl, "failed").Add(Node.Array(wid, reason));
                    row++;
                    budget.Fail();
                    var attempts = Node.Int(rec["attempts"]) + 1;
                    rec["attempts"] = attempts;
                    Event(wid, "failed", $"Its download failed ({reason}), {attempts} of 3 tries.");
                    rec["status"] = attempts < 3 ? "retry" : "failed";
                    rec["evidence"] = $"SteamCMD could not download it ({reason}), {attempts} of 3 tries";
                    L($"download of {wid} failed: {reason}");
                    if (SteamCmdWords.AccessDeniedRx().IsMatch(reason))
                    {
                        rec["status"] = "failed";          // this item, for now; Steam will not hand it out
                        denied++;
                    }
                    if (SteamCmdWords.StopRx().IsMatch(reason) || denied >= 2)
                    {
                        dl["stop"] = $"SteamCMD answered '{reason}'{(denied >= 2 ? " twice in a row" : "")} - no more downloads today";
                        budget.RefuseForToday($"SteamCMD answered '{reason}'");
                        break;
                    }
                    if (row >= 3)
                    {
                        dl["stop"] = "3 downloads in a row failed - no more in this run";
                        break;
                    }
                    if (reason.StartsWith(SteamCmdWords.Stall, StringComparison.Ordinal))
                    {
                        if (Stalled(wid, reason, budget))
                        {
                            dl["stop"] = $"SteamCMD stalled twice in this run ({reason}) - no more downloads in it";
                            break;
                        }
                        steam.Stop();                     // a fresh session for the next map: a stall alone is routine
                        started = false;
                    }
                    else if (reason.Contains("not running", StringComparison.Ordinal) || reason.Contains("stdin", StringComparison.Ordinal))
                    {
                        dl["stop"] = $"SteamCMD stopped answering ({reason})";
                        break;
                    }
                }
                done.Add(wid);
                State.Save();
                Clock.Sleep(Cfg.DownloadPause);
            }
        }
        finally
        {
            steam.Stop();
            State.Save();
        }
        dl["queued_left"] = todo.Count(w => !done.Contains(w));
        Limits(budget);
        return got;
    }
}
