// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>Why a player disliked a map: one mark per player per map, whose value in votes.json says why - every one of them that player's dislike
/// wherever likes count. A plain Dislike is "I did not like the map". Too big and Ads have their own effects (Sizes.cs); the rest are kept and
/// count as a dislike, nothing more. Chat steers it directly: !down big, !down 12 big. The same words on /act set the reason - the popup's click
/// too, "!down big", one command for both. Repeating a plain !down on a map already disliked keeps its reason and says so. A first plain !down opens
/// the popup that lists the reasons (Likes.cs, HudVote.WhyWords) for PopupSeconds: choosing one, or typing its word, updates that mark - without a
/// map, a reason goes to the map the popup asks about while it is open.</summary>
public static class DownReasons
{
    /// <summary>A reason: the word chat uses, the vote's value, what the popup says, and how a note names it.</summary>
    public sealed record Reason(string Word, int Value, string Label, string Short);

    public const int Plain = -1; // votes.json's plain downvote, since long before there were reasons

    /// <summary>How long the popup stands, and how long after that a click on its way still counts. The drain line runs a second shorter (HudView.PlayerClasses;
    /// the stylesheet's .why-run).</summary>
    public const double PopupSeconds = 15, PopupGraceSeconds = 2;

    public static readonly Reason[] All =
    [
        new("meh", Plain, "I did not like the map", "down"),
        new("big", HudCatalog.TooBigVote, "Map too big", "too big"),
        new("ads", HudCatalog.AdsVote, "Shows ads or sends players away", "ads"),
        new("small", HudCatalog.SmallVote, "Map too small", "too small"),
        new("broken", HudCatalog.BrokenVote, "Map is broken", "broken"),
        new("slow", HudCatalog.SlowVote, "Runs badly", "runs badly"),
    ];

    /// <summary>The reasons chat can name beyond the plain one, as a player reads them: "big, ads, small, broken or slow".</summary>
    public static string Words => string.Join(", ", All.Skip(1).SkipLast(1).Select(r => r.Word)) + " or " + All[^1].Word;

    public static Reason? Find(string word) => All.FirstOrDefault(r => r.Word == word);

    public static Reason? Of(int value) => All.FirstOrDefault(r => r.Value == value);

    /// <summary>What follows !down on the wire, split: "12 big" is that map and that reason, "big" the map that is on, "12" a plain vote on that map.</summary>
    public static (string Map, Reason? Reason) Split(string arg)
    {
        var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p switch
        {
            [var one] when Find(one) is { } r => ("", r),
            [var map, var word] when Find(word) is { } r => (map, r),
            _ => (arg.Trim(), null),
        };
    }

    /// <summary>What a chat line may say after !down, as the bridge takes it: "", a map, a reason, or both in either order - "12 big"; null when
    /// it is anything else.</summary>
    public static string? Normal(string low)
    {
        var p = low.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return p switch
        {
            [] => "",
            [var one] when Find(one) != null || Hud.IsMapArg(one) => one,
            [var map, var word] when Hud.IsMapArg(map) && Find(word) != null => $"{map} {word}",
            [var word, var map] when Hud.IsMapArg(map) && Find(word) != null => $"{map} {word}",
            _ => null,
        };
    }

    /// <summary>Whether a click may say this after !down: a reason, or a map and a reason. A map alone is every vote verb's own (Hud.IsAction).</summary>
    public static bool IsArg(string arg) => Normal(arg) is { Length: > 0 } n && n == arg && Split(arg).Reason != null;
}
