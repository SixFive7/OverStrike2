// Clicks from the TAB screen (HUD_TAB on the bridge, css_gghud tab <mode> live) - server side only, on the published layout: no new
// Workshop item or version.
//
// While TAB is held the client shows the board beside the scoreboard by itself (the layout's HUD_SCOREBOARD_VISIBLE rule). A custom HUD
// reports a click only while the server has captured that player's input (CS2's own scoreboard cursor does NOT reach it: no hover, and a
// left-click fires the gun), and capture also takes the player's mouse. So TAB works one way, down: the board's cursor from the moment
// TAB goes down until it comes up (CounterStrikeSharp's OnPlayerButtonsChanged; TAB is IN_SCORE, bit 33, which the client sets while the
// scoreboard is held). The buttons, WASD included, keep arriving while captured, and TAB coming up arrives at once. The scoreboard stays
// open; CS2's own "right-click to show the cursor" hint still shows, and a right-click removes the hint and keeps this cursor.
//
// TAB + E opens the menu window: the board's own "Open the menu" button as a key. The window takes the cursor over and stays open when TAB
// comes up. Off switches TAB off: the board shows while TAB is held, nothing takes the mouse.
//
// No time limit: a limit ended a TAB cursor with TAB still held, and that click went to the game as a shot. A TAB cursor ends on events
// only: TAB coming up, a hit, death, leaving, a map change, the menu window opening, the plugin unloading, and css_gghud release. One that
// ever sticks (a TAB-up the server never saw) goes with the next tap of TAB. Shared like Hud.cs: the bridge serves the mode and tests all
// of this; the plugin runs it. Plain BCL.
public sealed class HudTab
{
    public enum Mode { Off, Down }
    public enum Act { OpenMenu, CursorOn, CursorOff }

    public const ulong Scoreboard = 1UL << 33, Use = 1UL << 5; // CounterStrikeSharp's PlayerButtons: IN_SCORE, IN_USE
    public const Mode Default = Mode.Down; // HUD_TAB unset

    /// <summary>HUD_TAB's value: down, or off. The names of the modes TAB had before (hold, window, on, menu) all mean TAB on, so they read as
    /// down; anything else - a typo, an empty value from an older bridge - reads as off, so nothing takes a mouse by mistake.</summary>
    public static Mode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "down" or "hold" or "window" or "on" or "chord" or "board" or "menu" => Mode.Down,
        _ => Mode.Off,
    };

    public static string Name(Mode mode) => mode == Mode.Down ? "down" : "off";

    /// <summary>The board's command line (gg_b_cmds): says how TAB works while it does.</summary>
    public static string BoardLine(Mode mode) => mode == Mode.Down ? "Click a button  ·  TAB + E: the full menu" : "Hold TAB: the live board";

    sealed class Slot
    {
        public ulong Held;
        public bool Cursor; // the board's TAB cursor: on until an event ends it
    }

    readonly Dictionary<int, Slot> slots = new();

    Slot Of(int slot) => slots.TryGetValue(slot, out var s) ? s : slots[slot] = new Slot();

    /// <summary>The TAB cursor is on for this slot.</summary>
    public bool Cursor(int slot) => slots.TryGetValue(slot, out var s) && s.Cursor;

    public IEnumerable<int> CursorSlots() => slots.Where(x => x.Value.Cursor).Select(x => x.Key);

    /// <summary>A change of one player's buttons: what it asks for, if anything. Cheap on purpose - CounterStrikeSharp calls this inside
    /// the server's think for every button any player presses or lets go.</summary>
    public Act? Buttons(int slot, ulong pressed, ulong released, Mode mode, DateTime now)
    {
        var s = Of(slot);
        var had = s.Cursor;
        s.Held = (s.Held | pressed) & ~released;
        if ((released & Scoreboard) != 0)
        {
            if (!had) return null;
            s.Cursor = false;
            return Act.CursorOff;
        }
        if (mode == Mode.Off) return null;
        if ((pressed & Scoreboard) != 0 && !had) { s.Cursor = true; return Act.CursorOn; }
        if ((s.Held & Scoreboard) != 0 && (pressed & Use) != 0) { s.Cursor = false; return Act.OpenMenu; } // TAB + E: the window takes the cursor over
        return null;
    }

    /// <summary>Ends a slot's TAB cursor (a hit, death, the window taking over). True when it had one.</summary>
    public bool Release(int slot)
    {
        if (!slots.TryGetValue(slot, out var s) || !s.Cursor) return false;
        s.Cursor = false;
        return true;
    }

    /// <summary>Every TAB cursor off (css_gghud release, TAB switched off). Returns the slots that had one.</summary>
    public List<int> ReleaseAll()
    {
        var had = slots.Where(x => x.Value.Cursor).Select(x => x.Key).ToList();
        foreach (var s in slots.Values) s.Cursor = false;
        return had;
    }

    public void Gone(int slot) => slots.Remove(slot);

    /// <summary>A map change: everybody reconnects, nothing survives.</summary>
    public void MapStarted() => slots.Clear();
}
