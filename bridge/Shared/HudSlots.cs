// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>Who has what on screen, per player slot, kept free of CounterStrikeSharp so the bridge's --selftest can drive
/// it on a handed clock. The plugin feeds it events and asks Frame for the wanted view of every slot, then sends only the difference.
///
/// The window opens at every login once the player has spawned (or after WaitLimitSeconds) and stays until its X: it is the welcome, and the X is the
/// HUD check's first click (HudVersion.Button). Opened any other way it takes the mouse - and with it the player's movement - which is why it closes itself
/// after IdleCloseSeconds without a click. Damage, death and a respawn never close it: nothing protects a player while it is open but the vote's freeze. Everybody
/// gets the "now playing" card at their first spawn on each map.
///
/// Panorama cannot animate visibility, and a panel that turns visible and changes in the same update pops instead of fading. So each
/// thing shows in two steps a FadeSeconds apart: visible first (still transparent), then "in" (opaque, in place); and leaves the same way
/// round, "in" first, then invisible - while the mouse is let go at once.</summary>
public sealed class HudSlots
{
    public const double SpawnDelaySeconds = 1.5;  // after a spawn, let the HUD settle (and the client load the layout) first
    public const double WaitLimitSeconds = 20;    // a player who never spawns (spectating, picking a team) gets it anyway after this
    public const double ToastSeconds = 8;
    public const double IdleCloseSeconds = 30;    // the window goes by itself after this long without a click; the drain bar shows it (gungame_hud.css: 30s)
    public const double DrainAfterOpenSeconds = 1; // the idle line starts this long after the window opens: a transition on a panel only just shown does not run (a click restarts it after FadeSeconds)
    public const double FadeSeconds = 0.3;        // between the two steps of showing or hiding; more than one plugin frame (0.25 s)

    /// <summary>The window's pages. START: Start; MAPS: Maps (its browser, and the ballot while a vote is on); FUN: Fun; SERVER: Players, Game, Sync;
    /// HELP: Help, How. Players is v4.6.0's: an older content has no rule for its page class and shows an empty page there.</summary>
    public enum Page { Start, Maps, Game, Help, How, Fun, Sync, Players }

    /// <summary>The main tab a page belongs to, as the layout groups them.</summary>
    public static string GroupOf(Page page) => page switch
    {
        Page.Maps => "maps", Page.Fun => "fun", Page.Players or Page.Game or Page.Sync => "server", Page.Help or Page.How => "help", _ => "start",
    };

    /// <summary>What one slot should see now. Window/Toast: visible at all (fading included); WindowIn/ToastIn: fully shown. Toast: 0 none, 1 now playing.
    /// Draining: the bar that shows the idle close is running (it restarts by losing this for a moment). Returning: the player has been here before.</summary>
    public readonly record struct View(bool Window, bool WindowIn, bool Capture, Page Page, bool Draining, int Toast, bool ToastIn, bool Returning = false, bool Pinned = false);

    sealed class Slot
    {
        public bool Open;
        public Page Page;
        public DateTime OpenAt = DateTime.MaxValue;    // a window waiting for its moment
        public DateTime OpenedAt, ClosedAt, IdleUntil, DrainFrom;
        public DateTime? WaitingSince;                 // css_guide came for a player who has not spawned yet
        public bool Pinned;                            // the welcome: stays until its X, whatever else happens
        public bool Returning;                         // css_guide said this player has been here before
        public int Toast;
        public DateTime ToastFrom = DateTime.MaxValue, ToastUntil;
        public bool SeenMap;                           // had the now-playing card (or the welcome) on this map
    }

    readonly Dictionary<int, Slot> slots = new();

    public bool Idle => slots.Values.All(s => !s.Open && s.OpenAt == DateTime.MaxValue && s.WaitingSince == null && s.Toast == 0);

    Slot Of(int slot) => slots.TryGetValue(slot, out var s) ? s : slots[slot] = new Slot();

    /// <summary>css_guide from the bridge: a real join: the welcome window, on its start page, until its X. Right away (after the spawn delay) for a player who is
    /// alive, at their next spawn otherwise.</summary>
    public void Joined(int slot, bool first, bool alive, DateTime now)
    {
        var s = Of(slot);
        s.SeenMap = true;
        s.Returning = !first;
        s.Pinned = true;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        s.WaitingSince = null;
        if (alive) Schedule(s, now.AddSeconds(SpawnDelaySeconds));
        else s.WaitingSince = now;
    }

    public void Spawned(int slot, DateTime now)
    {
        var s = Of(slot);
        if (s.WaitingSince != null) { s.WaitingSince = null; Schedule(s, now.AddSeconds(SpawnDelaySeconds)); return; }
        if (s.SeenMap || s.Open) return;
        s.SeenMap = true;
        Card(s, 1, now.AddSeconds(SpawnDelaySeconds));
    }

    static void Schedule(Slot s, DateTime at) { s.OpenAt = at; s.Page = Page.Start; }

    static void Card(Slot s, int kind, DateTime at) { s.Toast = kind; s.ToastFrom = at; s.ToastUntil = at.AddSeconds(ToastSeconds); }

    /// <summary>A new map: every client reconnects, so nothing survives, and everybody is owed the now-playing card again.</summary>
    public void MapStarted() => slots.Clear();

    public void Gone(int slot) => slots.Remove(slot);

    /// <summary>The window on a given page, opened or kept open (a click's worth of idle time): the board's Open full menu, TAB + E, a
    /// Workshop link pasted in chat.</summary>
    public void Show(int slot, Page page, DateTime now)
    {
        var s = Of(slot);
        s.WaitingSince = null;
        s.Page = page;
        if (!s.Open) Open(s, now); else Touch(s, now);
    }

    public Page PageOf(int slot) => slots.TryGetValue(slot, out var s) ? s.Page : Page.Start;

    /// <summary>The HUD check (HudCheck): the welcome window again on a new map for a player who never clicked its X - after the spawn delay, instead of this spawn's
    /// card; nothing when it is open already.</summary>
    public void Check(int slot, DateTime now)
    {
        var s = Of(slot);
        s.SeenMap = true;
        s.WaitingSince = null;
        s.Pinned = true;
        if (s.Open) return;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        s.Page = Page.Start;
        s.OpenAt = now.AddSeconds(SpawnDelaySeconds);
    }

    /// <summary>The window closed, the welcome too (os_hud release, the way out if a window ever sticks).</summary>
    public void Close(int slot, DateTime now)
    {
        var s = Of(slot);
        s.WaitingSince = null;
        Close(s, now);
    }

    /// <summary>A vote's freeze is over: the window it held closes, unless it is the welcome, which goes back to its start page and waits for its X.</summary>
    public void VoteOver(int slot, DateTime now)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Open) return;
        if (s.Pinned) { s.Page = Page.Start; Touch(s, now); return; }
        Close(s, now);
    }

    /// <summary>A click in the window: closes it, switches the page, or (any other button) just keeps it open - the plugin turns the
    /// vote and action buttons into the bridge's actions itself (HudView.Command). Clicks from a slot without the window open are ignored.</summary>
    public void Click(int slot, string buttonId, DateTime now)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Open) return;
        if (buttonId == HudView.ButtonClose) { Close(s, now); return; }
        foreach (var (id, page) in HudView.Tabs) if (buttonId == id) s.Page = page;
        Touch(s, now);
    }

    public bool WindowOpen(int slot) => slots.TryGetValue(slot, out var s) && s.Open;

    void Open(Slot s, DateTime now)
    {
        s.Open = true; s.OpenAt = DateTime.MaxValue; s.OpenedAt = now;
        s.Toast = 0; s.ToastFrom = DateTime.MaxValue;
        Touch(s, now);
        s.DrainFrom = now.AddSeconds(DrainAfterOpenSeconds);
    }

    static void Touch(Slot s, DateTime now) { s.IdleUntil = now.AddSeconds(IdleCloseSeconds); s.DrainFrom = now.AddSeconds(FadeSeconds); }

    /// <summary>The TAB window (HudTab, mode window), every frame while TAB is held: no idle close and no drain bar while it is held,
    /// since nothing TAB opens ends on a timer. Once the hold ends the window closes with TAB, or (TAB + E) turns into the
    /// menu, whose Show starts its idle close afresh.</summary>
    public void Hold(int slot, DateTime now)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Open) return;
        s.IdleUntil = now.AddSeconds(IdleCloseSeconds);
        s.DrainFrom = DateTime.MaxValue;
    }

    static void Close(Slot s, DateTime now)
    {
        if (s.Open) s.ClosedAt = now;
        s.Open = false; s.OpenAt = DateTime.MaxValue; s.Pinned = false;
    }

    /// <summary>Every slot's wanted view at this moment. Advances the timers: a waiting window opens, an idle one closes, cards expire.</summary>
    public List<(int Slot, View View)> Frame(DateTime now)
    {
        var frame = new List<(int, View)>();
        foreach (var (id, s) in slots)
        {
            if (s.WaitingSince is { } w && (now - w).TotalSeconds >= WaitLimitSeconds) { s.WaitingSince = null; Schedule(s, now); }
            if (!s.Open && now >= s.OpenAt) Open(s, now);
            if (s.Open && !s.Pinned && now >= s.IdleUntil) Close(s, now);
            var fading = !s.Open && now < s.ClosedAt.AddSeconds(FadeSeconds);
            if (s.Toast != 0 && now >= s.ToastUntil.AddSeconds(FadeSeconds)) { s.Toast = 0; s.ToastFrom = DateTime.MaxValue; }
            var toast = s.Toast != 0 && now >= s.ToastFrom && !s.Open && !fading ? s.Toast : 0;
            frame.Add((id, new View(
                Window: s.Open || fading,
                WindowIn: s.Open && now >= s.OpenedAt.AddSeconds(FadeSeconds),
                Capture: s.Open,
                Page: s.Page,
                Draining: s.Open && !s.Pinned && now >= s.DrainFrom,
                Toast: toast,
                ToastIn: toast != 0 && now >= s.ToastFrom.AddSeconds(FadeSeconds) && now < s.ToastUntil,
                Returning: s.Returning, Pinned: s.Open && s.Pinned)));
        }
        return frame;
    }
}
