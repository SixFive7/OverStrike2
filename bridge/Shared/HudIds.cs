// The HUD's panel ids as each content names them. Ours - in the code, and in the layout of every content from v4.8.0 on - begin os_. The
// contents up to v4.7.0 have theirs baked in, the same ids beginning gg_, and a game keeps an older content for a while (Valve's review, Steam's
// next look for updates), so the plugin sends a panel under the name of every content still drawn for, and a click from an older content is
// made ours before anything reads it. The one place the shipped prefix is kept. Shared like Hud.cs: the bridge tests it in --selftest, the
// plugin's screen and clicks use it. Plain BCL only.
public static class HudIds
{
    /// <summary>Our prefix: every panel id in the code, and in the layouts from content v4.8.0 on.</summary>
    public const string Ours = "os_";

    /// <summary>The prefix the shipped contents before v4.8.0 have baked into their layouts.</summary>
    public const string Shipped = "gg_";

    /// <summary>The first content whose layout names its panels with ours, by its level (HudVersion.Contents).</summary>
    public const int OursFrom = 48;

    /// <summary>The prefix a content of this level has: ours from OursFrom on - a version newer than this code knows too (HudVersion.Newer).</summary>
    public static string PrefixOf(int level) => level >= OursFrom ? Ours : Shipped;

    /// <summary>The prefixes of the contents still drawn for, from HudVersion.Oldest to the server's own: what a panel for everybody goes out under.</summary>
    public static readonly string[] Drawn = HudVersion.Oldest < OursFrom ? [Ours, Shipped] : [Ours];

    /// <summary>The prefixes a game's HUD may have, by what the HUD check learned of it (HudFrame.Human.Version): its content's own, or every one
    /// still drawn for while the check has not said which ("" not seen yet, HudVersion.Unknown). An older content's gold button (HudVersion.Older)
    /// is one from before v4.6.0.</summary>
    public static string[] Of(string version) =>
        version == HudVersion.Older ? [Shipped]
        : HudVersion.Is(version) && version != HudVersion.Unknown ? [PrefixOf(HudVersion.LevelOf(version))]
        : Drawn;

    /// <summary>Our id as the content with this prefix names it.</summary>
    public static string As(string id, string prefix) =>
        prefix != Ours && id.StartsWith(Ours, StringComparison.Ordinal) ? prefix + id[Ours.Length..] : id;

    /// <summary>A content's id as ours: what a click from a shipped content names, made ours before the click is read.</summary>
    public static string ToOurs(string id) => id.StartsWith(Shipped, StringComparison.Ordinal) ? Ours + id[Shipped.Length..] : id;

    /// <summary>A panel no shipped content has - one of v4.8.0's own, the Bots page's (HudBots.Owns): it goes out under our name alone, never under a shipped
    /// content's, which would hold nothing under it; and a variable only such a panel's labels read (HudBots.OwnsVar) the same.</summary>
    public static bool OursOnly(string id) => HudBots.Owns(id);

    static bool Skip(string id, string prefix) => prefix != Ours && OursOnly(id);

    /// <summary>The screen the frame draws on (HudFrame.IScreen): each text, class, variable and picture goes to the screen under it once for
    /// every prefix prefixesFor(slot) gives - for everybody every content still drawn for, for a player the content their HUD check learned -
    /// but a panel of ours alone (OursOnly), which goes out under ours only. A class counts as sent only once it went out under every name it
    /// goes under, so one cut off by the frame's budget goes again in the next frame, and the screen under it sends no name twice.</summary>
    public sealed class Names(HudFrame.IScreen screen, Func<int, string[]> prefixesFor) : HudFrame.IScreen
    {
        public void Text(string id, string text, int slot = HudFrame.Everybody)
        {
            foreach (var prefix in prefixesFor(slot)) if (!Skip(id, prefix)) screen.Text(As(id, prefix), text, slot);
        }

        public bool Class(string id, string cls, bool on, int slot = HudFrame.Everybody)
        {
            var all = true;
            foreach (var prefix in prefixesFor(slot)) if (!Skip(id, prefix)) all &= screen.Class(As(id, prefix), cls, on, slot);
            return all;
        }

        public void Var(string id, string name, string text, int slot = HudFrame.Everybody)
        {
            foreach (var prefix in prefixesFor(slot)) if (prefix == Ours || !HudBots.OwnsVar(name)) screen.Var(As(id, prefix), name, text, slot);
        }

        public void Pictures(int slot, List<(string Panel, string[] Classes)> wanted)
        {
            foreach (var prefix in prefixesFor(slot)) screen.Pictures(slot, wanted.Select(w => (As(w.Panel, prefix), w.Classes)).ToList());
        }

        public void Capture(int slot, bool on) => screen.Capture(slot, on);
    }
}
