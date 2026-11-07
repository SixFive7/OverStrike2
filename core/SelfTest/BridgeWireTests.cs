using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

/// <summary>The words between the other programs and the bridge (BridgeWire.cs), written out: the bridge's parsers and the senders' encoders
/// both come from that file, so the bytes they agree on are held here - a change to them is a change to the wire, made on purpose.</summary>
static class BridgeWireTests
{
    public static void Run()
    {
        Check("bridge wire: the addresses, each a route of its own under the bridge's root, the secret after it",
            BridgeWire.Url("http://bridge:8080/", BridgeWire.RestartNotice, "abc123") == "http://bridge:8080/restart-notice/abc123"
            && BridgeWire.Route(BridgeWire.Ready) == "/ready/" && BridgeWire.Route(BridgeWire.Log) == "/log/"
            && new[] { BridgeWire.Log, BridgeWire.Health, BridgeWire.Ready, BridgeWire.Quitting, BridgeWire.RestartNotice, BridgeWire.Updated, BridgeWire.Removed, BridgeWire.Reload }
                .Distinct().Count() == 8);
        Check("bridge wire: the watcher's ready report - its head, the ids a line, the background download's line, its title one line",
            BridgeWire.ReadyBody(["901", "902"], BridgeWire.CurrentLine("903", 40, "A\ttitle\nwith breaks")) == "gg-ready\t1\n901\n902\ncurrent\t903\t40\tA title with breaks\n"
            && BridgeWire.ReadyBody([], null) == "gg-ready\t1\n" && BridgeWire.CurrentLine("903", null, "") == "current\t903\t-\t");
        Check("bridge wire: the quit notice and its cancel, with the watcher's own kind",
            BridgeWire.QuitBody("daily") == "gg-quit\t1\tdaily\n" && BridgeWire.QuitCancelBody("update") == "gg-quit\t1\tcancel\tupdate\n");
        Check("bridge wire: a restart coming, and taken back - the kinds and the whens as the bridge words them",
            BridgeWire.RestartBody(BridgeWire.GameUpdate, BridgeWire.AtMatchEnd, 0) == "gg-restart\t1\tgame-update\tmatch-end\t0\n"
            && BridgeWire.RestartBody(BridgeWire.Daily, BridgeWire.AtOnce, 30) == "gg-restart\t1\tdaily\tnow\t30\n"
            && BridgeWire.RestartCancelBody(BridgeWire.OurUpdate) == "gg-restart\t1\tcancel\tour-update\n" && BridgeWire.Downloads == "downloads");
        Check("bridge wire: a restart done, from what to what; the sync's verdicts a line each",
            BridgeWire.UpdatedBody(BridgeWire.GameUpdate, "1.41.9.4", "1.41.9.5") == "gg-updated\t1\tgame-update\t1.41.9.4\t1.41.9.5\n"
            && BridgeWire.RemovedBody([("5", "ok"), ("7", "banned")]) == "gg-removed\t1\n5\tok\n7\tbanned\n" && BridgeWire.RemovedBody([]) == "gg-removed\t1\n"
            && BridgeWire.HealthWord == "gg-health");
        Check("bridge wire: a body's fields after its message and version 1, trimmed; another message or version has none",
            BridgeWire.Fields("gg-quit\t1\t daily \r\nmore", BridgeWire.QuitWord) is ["daily"] && BridgeWire.Fields("gg-quit\t1", BridgeWire.QuitWord) is []
            && BridgeWire.Fields("gg-quit\t2\tdaily", BridgeWire.QuitWord) is null && BridgeWire.Fields("gg-restart\t1\tdaily", BridgeWire.QuitWord) is null
            && BridgeWire.Fields("", BridgeWire.QuitWord) is null);
    }
}
