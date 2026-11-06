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
        var session = Session(budget);
        var got = 0;
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
                if (!allowed && budget.DayOver)
                {
                    dl["stop"] = why;
                    break;
                }
                if (!allowed)
                {
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
                if (session.Ready(Clock.Now() - tWait) is { } noLogin)
                {
                    dl["stop"] = noLogin;
                    break;
                }
                var t = Clock.Now();
                var (good, reason, nbytes) = session.Download(wid);
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
                    session.Worked();
                    L($"downloaded {wid} {Text.Cut(Node.StrOr(rec["title"]), 40)} ({Text.Mb(size)}) in {Text.Fixed(Clock.Now() - t, 0)} s");
                }
                else
                {
                    List(dl, "failed").Add(Node.Array(wid, reason));
                    var attempts = Node.Int(rec["attempts"]) + 1;
                    rec["attempts"] = attempts;
                    Event(wid, "failed", $"Its download failed ({reason}), {attempts} of 3 tries.");
                    rec["status"] = attempts < 3 ? "retry" : "failed";
                    rec["evidence"] = $"SteamCMD could not download it ({reason}), {attempts} of 3 tries";
                    L($"download of {wid} failed: {reason}");
                    if (SteamCmdWords.AccessDeniedRx().IsMatch(reason))
                        rec["status"] = "failed";              // this item, for now; Steam will not hand it out
                    if (session.Failed(wid, reason) is { } stop)
                    {
                        dl["stop"] = stop;
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
            session.End();
            State.Save();
        }
        dl["queued_left"] = todo.Count(w => !done.Contains(w));
        Limits(budget);
        return got;
    }
}
