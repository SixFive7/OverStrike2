// Shared like Hud.cs: the bridge compiles it and tests it in --selftest, the plugin links it in. Plain BCL only.

/// <summary>The on-screen keyboard (a custom HUD has no text entry: Panel, Label, Image and Button only). Each key is a button whose id
/// says what it types; the plugin hands a click here and the player's query changes. Plain BCL.</summary>
public static class HudKeyboard
{
    public const string Open = "os_q_kbd", Back = "os_kb_back", Clear = "os_kb_clear", Space = "os_kb_space", Done = "os_kb_done";
    public const string Letters = "abcdefghijklmnopqrstuvwxyz", Digits = "1234567890";

    /// <summary>Every key's button id, in the keyboard's order: the digits, the letters, then space, backspace, clear and done.</summary>
    public static IEnumerable<string> Keys() =>
        Digits.Select(c => $"os_kb_{c}").Concat(Letters.Select(c => $"os_kb_{c}")).Concat([Space, Back, Clear, Done]);

    /// <summary>The character a key types, or null for the editing keys and anything else.</summary>
    public static char? CharOf(string buttonId) =>
        buttonId.Length == 7 && buttonId.StartsWith("os_kb_", StringComparison.Ordinal) && (Letters.Contains(buttonId[6]) || Digits.Contains(buttonId[6])) ? buttonId[6] : null;

    /// <summary>The query after one key: a character appended (up to HudSearch.MaxQuery), backspace, clear, or space (never two in a row,
    /// never first). Null when the button is not one of the keyboard's.</summary>
    public static string? Press(string query, string buttonId)
    {
        if (CharOf(buttonId) is { } ch) return query.Length < HudSearch.MaxQuery ? query + ch : query;
        return buttonId switch
        {
            Back => query.Length > 0 ? query[..^1] : "",
            Clear => "",
            Space => query.Length == 0 || query.EndsWith(' ') || query.Length >= HudSearch.MaxQuery ? query : query + " ",
            _ => null,
        };
    }
}
