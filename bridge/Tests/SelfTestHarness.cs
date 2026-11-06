// --selftest's shared tools: the live state as the plugin has it.

static partial class Program
{
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
