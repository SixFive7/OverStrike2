// The HUD's entity, and what it was sent: a frame sends only what changed, inside a budget of engine calls (FrameBudget), and marks what it
// touched for the network (MarkChanged).
using System.Diagnostics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

sealed partial class HudScreen
{
    int budget;                                       // engine calls this frame may still make; the rest go out in the next one
    const int FrameBudget = 600;

    // What the entity holds already, so a frame only sends differences. Cleared whenever the entity is new.
    readonly Dictionary<string, string> globalTexts = new();
    readonly Dictionary<(string, string), bool> globalClasses = new();
    readonly Dictionary<int, Dictionary<string, string>> playerTexts = new();
    readonly Dictionary<int, Dictionary<(string, string), bool>> playerClasses = new();
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

    void ForgetAll() { globalTexts.Clear(); globalClasses.Clear(); globalVars.Clear(); playerTexts.Clear(); playerClasses.Clear(); playerVars.Clear(); thumbs.Clear(); latestSent = 0; }

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

    readonly Dictionary<(string, string), string> globalVars = new();

    /// <summary>A named dialog variable on a panel every content has (gg_global): the layout's labels inherit it ({s:osver}).</summary>
    void Var(string id, string name, string text)
    {
        if (globalVars.TryGetValue((id, name), out var had) && had == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableString(id, name, Hud.Plain(text));
        globalVars[(id, name)] = text;
        globalDirty = true;
    }

    readonly Dictionary<int, Dictionary<(string, string), string>> playerVars = new();

    /// <summary>The same for one player alone (the map the popup asks about, on gg_screen).</summary>
    void Var(string id, string name, string text, CCSPlayerController player)
    {
        var mine = playerVars.TryGetValue(player.Slot, out var v) ? v : playerVars[player.Slot] = new();
        if (mine.TryGetValue((id, name), out var had) && had == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableStringForPlayer(player, id, name, Hud.Plain(text));
        mine[(id, name)] = text;
        playerDirty = true;
    }

    void Text(string id, string text, CCSPlayerController? player = null)
    {
        text = Hud.Plain(text);
        if (player == null)
        {
            if (globalTexts.TryGetValue(id, out var had) && had == text) return;
            if (!Spend()) return;
            entity!.SetDialogVariableString(id, "text", text);
            globalTexts[id] = text;
            globalDirty = true;
            return;
        }
        var mine = playerTexts.TryGetValue(player.Slot, out var t) ? t : playerTexts[player.Slot] = new();
        if (mine.TryGetValue(id, out var old) && old == text) return;
        if (!Spend()) return;
        entity!.SetDialogVariableStringForPlayer(player, id, "text", text);
        mine[id] = text;
        playerDirty = true;
    }

    /// <summary>Sets a class on a panel (for everybody, or for one player) unless it is so already. False when this frame's budget
    /// ran out before it could be sent.</summary>
    bool Class(string id, string cls, bool on, CCSPlayerController? player = null)
    {
        if (player == null)
        {
            if (globalClasses.TryGetValue((id, cls), out var had) && had == on) return true;
            if (!Spend()) return false;
            entity!.SetHasClass(id, cls, on);
            globalClasses[(id, cls)] = on;
            globalDirty = true;
            return true;
        }
        var mine = playerClasses.TryGetValue(player.Slot, out var c) ? c : playerClasses[player.Slot] = new();
        if (mine.TryGetValue((id, cls), out var old) && old == on) return true;
        if (!Spend()) return false;
        entity!.SetHasClassForPlayer(player, id, cls, on);
        mine[(id, cls)] = on;
        playerDirty = true;
        return true;
    }
}
