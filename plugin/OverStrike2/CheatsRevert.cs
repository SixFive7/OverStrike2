namespace OverStrike2;

/// <summary>The engine's revert of every cheat-flagged setting when cheats go off: the moment sv_cheats leaves 1, CS2 puts each convar flagged
/// FCVAR_CHEAT back to its own default - sv_infinite_ammo to 0, though Arms Race runs 2. So the block's own undo of a map's sv_cheats
/// (GameChangerBlock) is followed, within the block's next look, by every cheat-flagged setting on its list off its value, and by every such
/// fun choice of a player gone. Both are part of that one undo: put back without a notice of their own - the players read the one that names
/// cheats, which a notice of "the ammo" a second later replaced - and a player's choice comes back as they chose it. No CounterStrikeSharp type
/// in it: the self-test drives it.</summary>
public sealed class CheatsRevert
{
    public const string ConVar = "sv_cheats";

    /// <summary>How long after the block turned cheats off a cheat-flagged setting off its value counts as the engine's revert: the revert comes
    /// with the write or by the block's next look, a second later.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    DateTime at = DateTime.MinValue;
    readonly Dictionary<string, string> choices = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The block turned cheats off now. choices: the players' cheat-flagged fun choices as they were right before, which the revert
    /// takes and the block puts back.</summary>
    public void CheatsOff(DateTime now, IEnumerable<(string ConVar, string Value)> playersChoices)
    {
        at = now;
        choices.Clear();
        foreach (var (name, value) in playersChoices) choices[name] = value;
    }

    /// <summary>Whether the engine's revert may still be landing: within Window of the block turning cheats off.</summary>
    public bool Open(DateTime now) => now >= at && now - at <= Window;

    /// <summary>Whether putting this setting back now is part of the block's undo of cheats: a cheat-flagged setting while the revert is open.</summary>
    public bool Covers(string convar, bool cheatFlagged, DateTime now) => cheatFlagged && !convar.Equals(ConVar, StringComparison.OrdinalIgnoreCase) && Open(now);

    /// <summary>The players' cheat-flagged choices the revert took, while it is open and the player's choice stands: each with the value to put back.</summary>
    public List<(string ConVar, string Value)> Taken(Func<string, string?> value, Func<string, bool> stillChosen, DateTime now) =>
        !Open(now) ? [] : choices.Where(kv => stillChosen(kv.Key) && !Fun.Same(value(kv.Key), kv.Value)).Select(kv => (kv.Key, kv.Value)).ToList();
}
