// The HUD's entity, and what it was sent: the frame (HudFrame) says what is to be shown, and this sends only what changed, inside a budget of engine calls
// (FrameBudget), and marks what it touched for the network (MarkChanged).
using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

sealed partial class HudScreen : HudFrame.IScreen
{
    int budget;                                       // engine calls this frame may still make; the rest go out in the next one
    const int FrameBudget = 600;

    // What the entity holds already, so a frame only sends differences. Cleared whenever the entity is new.
    readonly Dictionary<string, string> globalTexts = new();
    readonly Dictionary<(string, string), bool> globalClasses = new();
    readonly Dictionary<(string, string), string> globalVars = new();
    readonly Dictionary<int, Dictionary<string, string>> playerTexts = new();
    readonly Dictionary<int, Dictionary<(string, string), bool>> playerClasses = new();
    readonly Dictionary<int, Dictionary<(string, string), string>> playerVars = new();
    readonly Dictionary<(int Slot, string Panel), HashSet<string>> thumbs = new(); // the picture classes a panel has on, for a player or for everybody (slot -1)
    readonly Dictionary<int, bool> captured = new();                                // whose mouse the HUD has, as last sent
    readonly Dictionary<int, CCSPlayerController> drawing = new();                  // slot -> the human this frame draws for there
    bool globalDirty, playerDirty, markBroken;           // what this frame changed, for MarkChanged

    // ---- the entity ----

    void EnsureEntity()
    {
        if (Live || !worldReady) return;
        var clock = Stopwatch.StartNew();
        try
        {
            var stale = 0;
            foreach (var old in Utilities.FindAllEntitiesByDesignerName<CCSCustomHudLayout>("custom_hud_layout"))
                if (old.IsValid && old.StrLayout == Hud.Layout) { old.Remove(); stale++; }
            entity = Utilities.CreateEntityByName<CCSCustomHudLayout>("custom_hud_layout");
            if (entity is not { IsValid: true }) { log.LogWarning("Over Strike 2 HUD: could not create custom_hud_layout"); entity = null; return; }
            entity.StrLayout = Hud.Layout;
            entity.DispatchSpawn();
            ForgetAll();
            log.LogInformation("Over Strike 2 HUD: layout entity {Index} spawned in {Ms} ms{Stale}", entity.Index, clock.ElapsedMilliseconds, stale > 0 ? $", {stale} leftover(s) removed" : "");
        }
        catch (Exception e)
        {
            entity = null;
            log.LogWarning("Over Strike 2 HUD: spawning the layout entity failed: {Message}", e.Message);
        }
    }

    /// <summary>Send this slot everything again. What its cards' pictures have on is kept, through a new player in the slot too: those
    /// classes may still be on in the client, and the next maps page switches off whatever it does not want (only a new entity starts
    /// clean).</summary>
    void Forget(int slot) { playerTexts.Remove(slot); playerClasses.Remove(slot); playerVars.Remove(slot); }

    void ForgetAll() { globalTexts.Clear(); globalClasses.Clear(); globalVars.Clear(); playerTexts.Clear(); playerClasses.Clear(); playerVars.Clear(); thumbs.Clear(); frame.Forget(); }

    /// <summary>CounterStrikeSharp's custom-HUD natives write the entity's networked vectors in place and notify nobody; CS2UIKit shows
    /// that clients see the changes anyway, but CS2KZ, which drives the same entity from C++, marks every change for the network
    /// explicitly. So after a frame that changed something, this marks the fields it touched as changed, the same way CounterStrikeSharp
    /// marks any entity field. Harmless when the engine would have noticed by itself; a first failure switches it off and is logged.</summary>
    void MarkChanged()
    {
        if ((!globalDirty && !playerDirty) || markBroken || !Live) { globalDirty = playerDirty = false; return; }
        try
        {
            foreach (var field in new[] { "m_vecPanelIds", "m_vecClassNames", "m_vecDialogVariableNames" })
                Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", field);
            if (globalDirty) Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", "m_globalLayoutState");
            if (playerDirty) Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", "m_vecPlayerLayoutStates");
        }
        catch (Exception e)
        {
            markBroken = true;
            log.LogWarning("Over Strike 2 HUD: marking the layout for the network failed, left to the engine from now on: {Message}", e.Message);
        }
        globalDirty = playerDirty = false;
    }

    /// <summary>One engine call's worth of this frame's budget: false once it is spent - the caller then skips the call without
    /// remembering it as sent, so the next frame sends it. A page change for a full server stays inside a few frames instead of one long one.</summary>
    bool Spend() => budget-- > 0;

    // ---- the screen the frame draws on (HudFrame.IScreen): for everybody (slot -1), or for the human the frame draws in that slot ----

    /// <summary>A dialog variable on a panel (gg_global's {s:osver}, gg_screen's popup words), which the layout's labels inherit.</summary>
    public void Var(string id, string name, string text, int slot = HudFrame.Everybody)
    {
        if (slot == HudFrame.Everybody)
        {
            if (globalVars.TryGetValue((id, name), out var had) && had == text) return;
            if (!Spend()) return;
            entity!.SetDialogVariableString(id, name, Hud.Plain(text));
            globalVars[(id, name)] = text;
            globalDirty = true;
            return;
        }
        if (!drawing.TryGetValue(slot, out var player)) return;
        var mine = playerVars.TryGetValue(slot, out var v) ? v : playerVars[slot] = new();
        if (mine.TryGetValue((id, name), out var old) && old == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableStringForPlayer(player, id, name, Hud.Plain(text));
        mine[(id, name)] = text;
        playerDirty = true;
    }

    public void Text(string id, string text, int slot = HudFrame.Everybody)
    {
        text = Hud.Plain(text);
        if (slot == HudFrame.Everybody)
        {
            if (globalTexts.TryGetValue(id, out var had) && had == text) return;
            if (!Spend()) return;
            entity!.SetDialogVariableString(id, "text", text);
            globalTexts[id] = text;
            globalDirty = true;
            return;
        }
        if (!drawing.TryGetValue(slot, out var player)) return;
        var mine = playerTexts.TryGetValue(slot, out var t) ? t : playerTexts[slot] = new();
        if (mine.TryGetValue(id, out var old) && old == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableStringForPlayer(player, id, "text", text);
        mine[id] = text;
        playerDirty = true;
    }

    /// <summary>Sets a class on a panel (for everybody, or for one player) unless it is so already. False when this frame's budget
    /// ran out before it could be sent.</summary>
    public bool Class(string id, string cls, bool on, int slot = HudFrame.Everybody)
    {
        if (slot == HudFrame.Everybody)
        {
            if (globalClasses.TryGetValue((id, cls), out var had) && had == on) return true;
            if (!Spend()) return false;
            entity!.SetHasClass(id, cls, on);
            globalClasses[(id, cls)] = on;
            globalDirty = true;
            return true;
        }
        if (!drawing.TryGetValue(slot, out var player)) return true;
        var mine = playerClasses.TryGetValue(slot, out var c) ? c : playerClasses[slot] = new();
        if (mine.TryGetValue((id, cls), out var old) && old == on) return true;
        if (!Spend()) return false;
        entity!.SetHasClassForPlayer(player, id, cls, on);
        mine[(id, cls)] = on;
        playerDirty = true;
        return true;
    }

    /// <summary>The pictures for a player, or for everybody (two classes a picture, HudThumbs): what a panel's previous picture had and this one does not goes off
    /// first, and only then does the new pair come on - a card never holds parts of two pictures at once, which could show the wrong map's.</summary>
    public void Pictures(int slot, List<(string Panel, string[] Classes)> wanted)
    {
        foreach (var (panel, want) in wanted)
        {
            var key = (slot, panel);
            var on = thumbs.TryGetValue(key, out var had) ? had : thumbs[key] = new HashSet<string>();
            var cleared = true;
            foreach (var cls in on.Where(c => !want.Contains(c)).ToList())
            {
                if (!Class(panel, cls, false, slot)) { cleared = false; break; } // out of budget: the rest next frame
                on.Remove(cls);
            }
            if (!cleared) continue;
            foreach (var cls in want)
                if (Class(panel, cls, true, slot)) on.Add(cls); else break;
        }
    }

    /// <summary>The HUD takes this player's mouse, or gives it back, when that changed.</summary>
    public void Capture(int slot, bool on)
    {
        if (captured.GetValueOrDefault(slot) == on || !drawing.TryGetValue(slot, out var player)) return;
        entity!.SetInputCaptureEnabled(player, on);
        captured[slot] = on;
        playerDirty = true;
    }
}
