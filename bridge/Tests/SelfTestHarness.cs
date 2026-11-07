// --selftest's shared tools: the live state as the plugin has it, and the sync's report as it sends one.

static partial class Program
{
    /// <summary>The Workshop sync's report of these verdicts, as it posts it (core's BridgeWire.RemovedBody).</summary>
    static string RemovedBody(IEnumerable<(long Id, string Verdict)> verdicts) => BridgeWire.RemovedBody(verdicts.Select(v => (v.Id.ToString(), v.Verdict)));

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
}
