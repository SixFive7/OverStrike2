// Clicks from the TAB screen (HUD_TAB on the bridge, css_gghud tab <mode> live) - server side only, on the published layout: no new
// Workshop item or version.
//
// While TAB is held the client shows the board beside the scoreboard by itself (the layout's HUD_SCOREBOARD_VISIBLE rule); its one
// button is "Open the menu". A custom HUD reports a click only while the server has captured that player's input (CS2's own scoreboard
// cursor does NOT reach it: tried 2026-10-11 - no hover, and a left-click fires the gun), and capture also takes the player's mouse.
// When to capture during TAB was an experiment, switched live by css_gghud tab and read off the player's buttons (CounterStrikeSharp's
// OnPlayerButtonsChanged; TAB is IN_SCORE, bit 33, which the client sets while the scoreboard is held). The owner tried down, hold and
// window in game and all three work: the buttons, WASD included, keep arriving while captured, and TAB
// coming up arrives at once. The modes:
//
//   down   (the default since then) the cursor from the moment TAB goes down until it comes up. The scoreboard stays open; CS2's own
//          "right-click to show the cursor" hint still shows, and a right-click removes the hint and keeps this cursor;
//   hold   the cursor once TAB has been held HoldSeconds - a quick look at the scores gets none;
//   window the menu window from the moment TAB goes down until it comes up (centered by the layout: beside the scoreboard would need a
//          new layout), its idle close held off meanwhile (HudSlots.Hold);
//   on     TAB + right-click - CS2 keeps that right-click for its own cursor and never sends it, so only TAB + E works;
//   menu   TAB + E only; off.
// TAB + E opens the menu window in every mode but off.
//
// No time limit (the owner's decision of 2026-10-21): the 10 s one there was ended a TAB cursor with TAB still held, and
// that click went to the game as a shot. A TAB cursor or window ends on events only: TAB coming up, a hit, death, leaving, a map change,
// the menu window opening, the plugin unloading, and css_gghud release. One that ever sticks (a TAB-up the server never saw) goes with
// the next tap of TAB. Shared like Hud.cs: the bridge serves the mode and tests all of this; the plugin runs it. Plain BCL.
public sealed class HudTab
{
    public enum Mode { Off, Menu, On, Down, Hold, Window }
    public enum Act { OpenMenu, CursorOn, CursorOff, OpenWindow, CloseWindow, HoldCheck }

    public const ulong Scoreboard = 1UL << 33, Use = 1UL << 5, Attack2 = 1UL << 11; // CounterStrikeSharp's PlayerButtons
    public const double HoldSeconds = 0.3;
    public const Mode Default = Mode.Down; // HUD_TAB unset (since 2026-10-21); an unreadable value is off

    public static Mode Parse(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "menu" => Mode.Menu,
        "on" or "chord" or "board" => Mode.On,
        "down" => Mode.Down,
        "hold" => Mode.Hold,
        "window" => Mode.Window,
        _ => Mode.Off,
    };

    public static string Name(Mode mode) => mode switch
    {
        Mode.Menu => "menu", Mode.On => "on", Mode.Down => "down", Mode.Hold => "hold", Mode.Window => "window", _ => "off",
    };

    /// <summary>The board's command line (gg_b_cmds): says how the mode works while it does.</summary>
    public static string BoardLine(Mode mode) => mode switch
    {
        Mode.Menu => "TAB + E: the full menu",
        Mode.On => "TAB + right-click: mouse  ·  TAB + E: the full menu",
        Mode.Down or Mode.Hold => "Click a button  ·  TAB + E: the full menu",
        Mode.Window => "Hold TAB: the menu, with the mouse",
        _ => "Hold TAB: the live board",
    };

    /// <summary>The names of a button mask, for the log: what the client sent while a TAB cursor or window was up.</summary>
    public static string Names(ulong buttons)
    {
        if (buttons == 0) return "-";
        var names = new List<string>();
        foreach (var (bit, name) in new (ulong, string)[] { (1UL, "LMB"), (Attack2, "RMB"), (Scoreboard, "TAB"), (Use, "E"), (1UL << 3, "W"), (1UL << 4, "S"),
                     (1UL << 9, "A"), (1UL << 10, "D"), (1UL << 1, "JUMP"), (1UL << 2, "DUCK"), (1UL << 13, "R"), (1UL << 16, "SHIFT"), (1UL << 35, "F") })
            if ((buttons & bit) != 0) { names.Add(name); buttons &= ~bit; }
        if (buttons != 0) names.Add("0x" + buttons.ToString("X"));
        return string.Join("+", names);
    }

    sealed class Slot
    {
        public ulong Held;
        public bool Cursor;                     // the board's TAB cursor: on until an event ends it
        public DateTime TabDownAt = DateTime.MaxValue;
        public bool Window;                     // the menu window is open because TAB went down (mode window): it closes when TAB comes up
    }

    readonly Dictionary<int, Slot> slots = new();

    Slot Of(int slot) => slots.TryGetValue(slot, out var s) ? s : slots[slot] = new Slot();

    /// <summary>The TAB cursor is on for this slot.</summary>
    public bool Cursor(int slot) => slots.TryGetValue(slot, out var s) && s.Cursor;

    public IEnumerable<int> CursorSlots() => slots.Where(x => x.Value.Cursor).Select(x => x.Key);

    /// <summary>The menu window is open because TAB went down (mode window).</summary>
    public bool Window(int slot) => slots.TryGetValue(slot, out var s) && s.Window;

    /// <summary>Worth logging this slot's buttons: a TAB cursor or window is up, or TAB is held in a mode that captures on TAB.</summary>
    public bool Watching(int slot, Mode mode) => slots.TryGetValue(slot, out var s)
        && (s.Cursor || s.Window || (mode is Mode.Down or Mode.Hold or Mode.Window && (s.Held & Scoreboard) != 0));

    /// <summary>A change of one player's buttons: what it asks for, if anything. Cheap on purpose - CounterStrikeSharp calls this inside
    /// the server's think for every button any player presses or lets go.</summary>
    public Act? Buttons(int slot, ulong pressed, ulong released, Mode mode, DateTime now)
    {
        var s = Of(slot);
        var had = s.Cursor;
        s.Held = (s.Held | pressed) & ~released;
        if ((released & Scoreboard) != 0)
        {
            s.TabDownAt = DateTime.MaxValue;
            if (s.Window) { s.Window = false; s.Cursor = false; return Act.CloseWindow; }
            if (had) { s.Cursor = false; return Act.CursorOff; }
            return null;
        }
        if (mode == Mode.Off) return null;
        if ((pressed & Scoreboard) != 0)
        {
            s.TabDownAt = now;
            switch (mode)
            {
                case Mode.Down when !had: s.Cursor = true; return Act.CursorOn;
                case Mode.Hold: return Act.HoldCheck;
                case Mode.Window when !s.Window: s.Window = true; return Act.OpenWindow;
            }
        }
        if ((s.Held & Scoreboard) == 0) return null;
        if ((pressed & Use) != 0) { s.Cursor = false; s.Window = false; return Act.OpenMenu; } // TAB + E, in every mode
        if (mode == Mode.On && !had && (pressed & Attack2) != 0) { s.Cursor = true; return Act.CursorOn; }
        return null;
    }

    /// <summary>Mode hold: HoldSeconds after TAB went down. The cursor, if TAB is still held.</summary>
    public Act? Hold(int slot, Mode mode, DateTime now)
    {
        if (mode != Mode.Hold || !slots.TryGetValue(slot, out var s)) return null;
        if ((s.Held & Scoreboard) == 0 || s.Cursor || (now - s.TabDownAt).TotalSeconds < HoldSeconds - 0.02) return null;
        s.Cursor = true;
        return Act.CursorOn;
    }

    /// <summary>Ends a slot's TAB cursor and forgets its TAB window (a hit, death, the window taking over). True when it had either -
    /// the plugin closes the window itself.</summary>
    public bool Release(int slot)
    {
        if (!slots.TryGetValue(slot, out var s) || (!s.Cursor && !s.Window)) return false;
        s.Cursor = false;
        s.Window = false;
        return true;
    }

    /// <summary>Every TAB cursor and window off (css_gghud release, the mode switched off). Returns the slots that had one.</summary>
    public List<int> ReleaseAll()
    {
        var had = slots.Where(x => x.Value.Cursor || x.Value.Window).Select(x => x.Key).ToList();
        foreach (var s in slots.Values) { s.Cursor = false; s.Window = false; }
        return had;
    }

    public void Gone(int slot) => slots.Remove(slot);

    /// <summary>A map change: everybody reconnects, nothing survives.</summary>
    public void MapStarted() => slots.Clear();
}
