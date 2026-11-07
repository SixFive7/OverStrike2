// The sync's self-test: the verdicts.
using System.Text.Json.Nodes;
using static OverStrike2.Sync.SelfTest.T;
using static OverStrike2.Sync.SelfTest.W;

namespace OverStrike2.Sync.SelfTest;

/// <summary>The verdicts: from the scan and the test loads to pass, crasher, kick, advert or non-map.</summary>
static class VerdictTests
{
    public const string N = "2026-11-01";

    static (string Kind, JsonObject Detail) Fv(string rec) => Verdicts.Final(O(rec), N);

    // ---- verdicts
    internal static void VerdictChecks()
    {
        const string ok = "\"scan\": {\"verdict\": \"map\", \"maps\": [\"x\"], \"advert\": null}";
        Check("verdict: a pass", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"pass\", \"level\": \"x\", \"mode\": \"armsrace\"}}]}}").Kind == "pass");
        Check("verdict: a pass after a crash is a pass", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"crash\"}}, {{\"verdict\": \"pass\", \"level\": \"x\"}}]}}").Kind == "pass");
        Check("verdict: one crash is not a verdict yet", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"crash\"}}]}}").Kind == "pending");
        var (k, d) = Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"crash\"}}, {{\"verdict\": \"crash\"}}, {{\"verdict\": \"crash\"}}]}}");
        Check("verdict: 3 of 3 crashes is a crasher with crashes 3", k == "crasher" && Node.Int(d["crashes"]) == 3 && Node.Str(d["evidence"]).Contains("all 3"));
        (k, d) = Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"freeze\"}}, {{\"verdict\": \"rejected\"}}, {{\"verdict\": \"rejected\"}}]}}");
        Check("verdict: 1 freeze of 3 is a crasher with crashes 1", k == "crasher" && Node.Int(d["crashes"]) == 1 && Node.Str(d["evidence"]).Contains("1 of 3"));
        Check("verdict: 3 error maps make a non-map", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"errormap\"}}, {{\"verdict\": \"errormap\"}}, {{\"verdict\": \"errormap\"}}]}}").Kind == "nonmap");
        var stub = "{\"verdict\": \"rejected\", \"level\": \"de_dust2\"}";
        Check("verdict: a stock map instead of its own is a stub", Node.Str(Fv($"{{{ok}, \"loads\": [{stub}, {stub}, {stub}]}}").Detail["kind"]) == "stub");
        var abort = "{\"verdict\": \"abort\"}";
        Check("verdict: an aborted load does not count", Fv($"{{{ok}, \"loads\": [{abort}, {abort}, {abort}, {abort}, {abort}]}}").Kind == "pending");
        Check("verdict: a pack never loads", Fv("{\"scan\": {\"verdict\": \"pack\", \"models\": 3}}").Kind == "nonmap");
        (k, d) = Fv("{\"scan\": {\"verdict\": \"map\", \"maps\": [\"x\"], \"advert\": \"chat advert\", \"advert_sample\": \"say connect 203.0.113.4\"}, \"loads\": [{\"verdict\": \"pass\"}]}");
        Check("verdict: a loading map with a chat advert goes on broken.json", k == "advert" && Node.Str(d["reason"]) == "advertises another server");
        (k, _) = Fv("{\"scan\": {\"verdict\": \"map\", \"maps\": [\"x\"], \"advert\": \"server connect\"}, \"loads\": [{\"verdict\": \"pass\", \"level\": \"x\"}]}");
        Check("verdict: a server-side connect alone is still a pass", k == "pass");
        (k, _) = Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"pass\", \"chat\": [\"connect 203.0.113.8:27015\"]}}]}}");
        Check("verdict: an advert seen in chat on a test load counts", k == "advert");
        Check("verdict: kick", Fv($"{{{ok}, \"loads\": [{{\"verdict\": \"kick\"}}]}}").Kind == "kick");
    }
}
