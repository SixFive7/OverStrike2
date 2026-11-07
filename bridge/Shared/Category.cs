// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only. Part of the message catalogue (Messages.cs).

/// <summary>A message's category: its id on the wire and the name its tab and eyebrow show (Messages.Categories).</summary>
public sealed record Category(string Id, string Name)
{
    /// <summary>The class that colours the top line's tab, the window line's bar and eyebrow.</summary>
    public string Class => "cat-" + Id;
}
