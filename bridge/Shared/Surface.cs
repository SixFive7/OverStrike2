// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only. Part of the message catalogue (Messages.cs).

/// <summary>Where a message shows. Window: the window on its own page. Line: a line on the window's page (the player's own, or the page's own place for it).
/// Card: our card under the crosshair. Plain: CS2's plain centre text. Alert: CS2's alert text. Banner: the status banner. Top: the top line. Countdown: the
/// countdown banner. None: no surface of its own, chat or the board only.</summary>
public enum Surface { None, Window, Line, Card, Plain, Alert, Banner, Top, Countdown }
