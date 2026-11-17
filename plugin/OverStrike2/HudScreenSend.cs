// The HUD's entity, and what it was sent: the frame (HudFrame) says what is to be shown, and this sends only what changed, inside a budget of engine calls
// (FrameBudget), and marks what it touched for the network (MarkChanged).
using System.Diagnostics;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Memory;
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

    /// <summary>Everything the entity holds for this slot goes - its classes and its words - so the new player in it starts clean. The natives only ever add an entry or
    /// change one (CounterStrikeSharp's customhudlayout.cpp: an entry per panel and class, or panel and variable, in the slot's layout state), and a joining game is handed
    /// the slot's entries as its last occupant left them: a v4.13.0 game that took the slot of a v4.14.0 one on the same map logged "Unable to find panel" for the 36
    /// panels of the chat column, which it does not have, at the moment it entered (the live lab, release 218). The slot's state is where the natives find it (element
    /// slot of m_vecPlayerLayoutStates, the schema's size apart), and is touched only once that layout is checked (SlotStatesOk). True when the slot holds nothing of
    /// anybody's now.</summary>
    bool ClearSlot(int slot)
    {
        try
        {
            var vector = entity!.Handle + Schema.GetSchemaOffset("CCSCustomHudLayout", "m_vecPlayerLayoutStates");
            var count = NativeAPI.GetNetworkVectorSize(vector);
            if (slot < 0 || slot >= count) return true; // no state for this slot yet: nothing of anybody's
            var size = Schema.GetClassSize("CCSCustomHudLayoutState");
            var first = count <= 128 && size > 0 ? NativeAPI.GetNetworkVectorElementAt(vector, 0) : IntPtr.Zero;
            if (!SlotStatesOk(first, count, size)) return false;
            var state = new CCSCustomHudLayoutState(first + slot * size);
            if (state.HasClasses.Count == 0 && state.DialogVariableStrings.Count == 0) return true;
            state.HasClasses.RemoveAll();
            state.DialogVariableStrings.RemoveAll();
            foreach (var key in thumbs.Keys.Where(k => k.Slot == slot).ToList()) thumbs.Remove(key); // its pictures' classes went with the rest
            playerDirty = true;
            return true;
        }
        catch (Exception e)
        {
            return SlotStatesTold(false, $"clearing slot {slot} failed: {e.Message}");
        }
    }

    /// <summary>Whether the layout states lie as ClearSlot reads them: count elements of size bytes from first, every one beginning with the same class pointer
    /// (CCSCustomHudLayoutState has virtual members) - checked again whenever the vector's memory or length moves, so one player in slot 0 checks all of them.</summary>
    bool SlotStatesOk(IntPtr first, int count, int size)
    {
        if (first == IntPtr.Zero) return SlotStatesTold(false, $"{count} states of {size} bytes, none found");
        if (statesChecked == (first, count, size)) return statesOk;
        var kind = Marshal.ReadIntPtr(first);
        var lined = kind != IntPtr.Zero && Enumerable.Range(1, count - 1).All(i => Marshal.ReadIntPtr(first + i * size) == kind);
        var own = lined ? Enumerable.Range(0, count).Count(i => new CCSCustomHudLayoutState(first + i * size).PlayerSlot == i) : 0;
        statesChecked = (first, count, size);
        return SlotStatesTold(lined, lined ? $"{count} states of {size} bytes, {own} naming their own slot" : $"{count} states of {size} bytes that do not line up");
    }

    (IntPtr First, int Count, int Size)? statesChecked; // the layout states SlotStatesOk last checked
    bool statesOk;
    bool? statesTold;                                    // the verdict last logged

    bool SlotStatesTold(bool ok, string what)
    {
        statesOk = ok;
        if (statesTold == ok) return ok;
        statesTold = ok;
        if (ok) log.LogInformation("Over Strike 2 HUD: a new player's slot starts clean - the layout's {What}", what);
        else log.LogWarning("Over Strike 2 HUD: a new player's slot keeps its last occupant's HUD entries - the layout's {What}", what);
        return ok;
    }


    /// <summary>Send this slot everything again. What its cards' pictures have on is kept: those classes may still be on in the client, and the next
    /// maps page switches off whatever it does not want (only a new entity starts clean, and a slot a new player takes: ClearSlot).</summary>
    void Forget(int slot) { playerTexts.Remove(slot); playerClasses.Remove(slot); playerVars.Remove(slot); }

    void ForgetAll() { globalTexts.Clear(); globalClasses.Clear(); globalVars.Clear(); playerTexts.Clear(); playerClasses.Clear(); playerVars.Clear(); thumbs.Clear(); frame.Forget(); }

    /// <summary>CounterStrikeSharp's custom-HUD natives write the entity's networked vectors in place and notify nobody; CS2UIKit shows
    /// that clients see the changes anyway, but CS2KZ, which drives the same entity from C++, marks every change for the network
    /// explicitly. So after a frame that changed something, this marks the fields it touched as changed, the same way CounterStrikeSharp
    /// marks any entity field. Harmless when the engine would have noticed by itself; a first failure switches it off and is logged. Never the
    /// global state (m_globalLayoutState): an embedded CCSCustomHudLayoutState at offset 2064, not a field the engine can resolve - it wrote "Couldn't
    /// resolve offset 2064 in CCSCustomHudLayout at path (-1 = '')" for each such mark, about 45 server console lines a minute with a client on, and
    /// dropped it ("requested resolve all 5 changes, actually resolved only 4"). So that mark never did anything: the global state reaches the
    /// clients without it, as the HUD has shown all along.</summary>
    void MarkChanged()
    {
        if ((!globalDirty && !playerDirty) || markBroken || !Live) { globalDirty = playerDirty = false; return; }
        try
        {
            foreach (var field in new[] { "m_vecPanelIds", "m_vecClassNames", "m_vecDialogVariableNames" })
                Utilities.SetStateChanged(entity!, "CCSCustomHudLayout", field);
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

    /// <summary>A dialog variable on a panel (os_global's {s:osver}, os_screen's popup words), which the layout's labels inherit.</summary>
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
