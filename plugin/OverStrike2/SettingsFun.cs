using System.Diagnostics;
using System.Text.Json;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace OverStrike2;

/// <summary>The settings module's fun page: the players' changes (gg_fun), the effects that are no convar, and their undo.</summary>
sealed partial class Settings
{
    // ---- the fun page's changes (Fun.cs) ----
    readonly FunState fun = new();
    string lastFun = "none yet";
    readonly Dictionary<string, string> setBy = new(StringComparer.OrdinalIgnoreCase); // setting id -> who set it, while it is off its default (the HUD's Fun page says it)

    /// <summary>Who set a fun setting that is off its default (gg_fun's last argument), or null: at its default, or not known.</summary>
    public string? SetBy(string settingId) => setBy.GetValueOrDefault(settingId);
    /// <summary>The fun page's effects that are no game convar (Fun.Vampire, Fun.Ghost, Fun.NoRecoil, Fun.Drops, Fun.CatchUp): "1" or "0", kept
    /// here like a convar; one not in it holds its default.</summary>
    readonly Dictionary<string, string> effects = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An effect's default: on for those of Fun.EffectsOn (the grenade drops, catch-up), off for every other.</summary>
    static string EffectDefault(string name) => Fun.EffectsOn.FirstOrDefault(e => e.Effect.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? "0";

    /// <summary>Every effect the fun page can switch.</summary>
    static IEnumerable<string> EffectNames => Fun.AllConVars.Where(Fun.IsEffect);

    /// <summary>An effect changed (a player's change, the undo at a map's end, the reset): its name and whether it is on now. The plugin hands
    /// the grenade drops and catch-up to their modules through it; the Effects module looks for itself.</summary>
    public event Action<string, bool>? EffectChanged;

    /// <summary>The map is ending (from its OnMapEnd to the next map's start): an effect put back now must not act on the players of the map
    /// that goes - no grenade is handed out then.</summary>
    public bool MapEnding { get; private set; }

    /// <summary>What a fun setting's convar or effect holds now: the HUD's fun page reads it, the Effects module asks whether one is on.</summary>
    public string? FunValue(string name) => Value(name);

    public bool EffectOn(string name) => Fun.Same(Value(name), "1");

    /// <summary>Every effect back at its default (after the players' changes are undone at a map's end and at the reset): whatever left one
    /// elsewhere - a reload of the plugin keeps none - this is where they all are afterwards. Returns what it changed.</summary>
    List<string> EffectsBack()
    {
        var changed = new List<string>();
        foreach (var name in EffectNames)
        {
            var want = EffectDefault(name);
            if (Fun.Same(Value(name), want)) continue;
            Write(name, want);
            changed.Add($"{name} -> {want}");
        }
        return changed;
    }


    // ---- the fun page: gg_fun (Fun.cs) ----------------------------------------------------------------------------------------

    /// <summary>Every player change undone (the map's end, the bridge's reset after nobody has been on): each changed convar back to what it
    /// was before the first player change on this map, and every effect back at its default - the grenade drops and catch-up on.</summary>
    string UndoFun(string when)
    {
        setBy.Clear(); // every setting goes back to its default: nobody set any
        var writes = fun.Reset(Value);
        var done = writes.Select(w => $"{w.ConVar} -> {w.Value}{(Write(w.ConVar, w.Value) ? "" : " (refused)")}").ToList();
        done.AddRange(EffectsBack().Select(b => b + " (its default)")); // after the undo: every effect where it starts, whatever left it elsewhere
        if (done.Count == 0) return "no player changes to undo";
        lastFun = $"{DateTime.Now:HH:mm:ss} {when}: undone {string.Join(", ", done)}";
        log.LogInformation("Over Strike 2 settings: fun settings undone at {When}: {List}", when, string.Join(", ", done));
        return $"undone {string.Join(", ", done)}";
    }

    /// <summary>gg_fun &lt;setting&gt; &lt;choice&gt; ["who"]: a player's change from the menu's fun page or chat (the HUD runs it, naming who
    /// asked; once the value has landed its message says who switched what: Messages.FunChanged). gg_fun reset
    /// ["who"]: every player change undone and every effect at its default - the bridge's reset after 10 minutes with nobody on, and the
    /// page's Default. Bare gg_fun: what each setting shows now. Server only: no chat trigger reaches it.</summary>
    [ConsoleCommand(Fun.Command, "Fun settings: gg_fun <setting> <choice> [who] | gg_fun reset [who] | gg_fun")]
    [CommandHelper(minArgs: 0, usage: "[<setting> <choice> | reset] [who]", whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnFun(CCSPlayerController? player, CommandInfo command)
    {
        var verb = command.ArgCount > 1 ? command.GetArg(1).ToLowerInvariant() : "";
        if (verb == "")
        {
            command.ReplyToCommand("fun: " + string.Join(", ", Fun.Settings.Select(s => $"{s.Id} {Fun.Showing(s, Value).Id}")) + $" ({fun.Describe(Value)}); last: {lastFun}");
            return;
        }
        if (verb == "reset")
        {
            command.ReplyToCommand($"fun: reset - {ResetFun(command.ArgCount > 2 ? command.GetArg(2) : "Console")}");
            return;
        }
        if (Fun.Find(verb) is not { } setting || command.ArgCount < 3 || Fun.Find(setting, command.GetArg(2)) is not { } choice)
        {
            command.ReplyToCommand("fun: gg_fun <setting> <choice> [who] - " + string.Join("; ", Fun.Settings.Select(s => $"{s.Id} {string.Join("|", s.Choices.Select(c => c.Id))}")));
            return;
        }
        command.ReplyToCommand($"fun: {ChangeFun(setting, choice, command.ArgCount > 3 ? command.GetArg(3) : "Console")}");
    }

    /// <summary>A change of a fun setting, made now: what gg_fun &lt;setting&gt; &lt;choice&gt; does, and what the HUD does in process for a player's click on the Fun page
    /// or their chat command (HudScreen.FunSet), a frame after it. Its convars or its effect written, who set it kept while it is off its default; a Clash refused
    /// (the page and chat say it before they get here). Returns what was done, in gg_fun's words.</summary>
    public string ChangeFun(Fun.Setting setting, Fun.Choice choice, string by)
    {
        if (mode == "off") return "refused - mapreset_mode is off";
        if (Fun.Refusal(setting, choice, Value) is { } why)
        {
            log.LogInformation("Over Strike 2 settings: fun on {Map}: {Setting} {Choice} by {By} refused - {Why}", map, setting.Id, choice.Id, by, why);
            return $"refused - {why}";
        }
        var writes = fun.Apply(setting, choice, Value);
        var refused = writes.Where(w => !Write(w.ConVar, w.Value)).Select(w => w.ConVar).ToList();
        if (choice == setting.Normal) setBy.Remove(setting.Id); else if (refused.Count == 0) setBy[setting.Id] = by; // who set it, until it is back at its default
        var line = $"{setting.Id} {choice.Id} by {by}: {(writes.Count == 0 ? "nothing to change" : string.Join(", ", writes.Select(w => $"{w.ConVar} {w.Value}")))}{(refused.Count > 0 ? $"; refused {string.Join(", ", refused)}" : "")}";
        lastFun = $"{DateTime.Now:HH:mm:ss} {line}";
        log.LogInformation("Over Strike 2 settings: fun on {Map}: {Line}", map, line);
        return line;
    }

    /// <summary>The Fun page's Default, made now: every player change undone and every effect at its default, as gg_fun reset does (HudScreen.FunReset).</summary>
    public string ResetFun(string by) => UndoFun($"reset by {by}");
}
