using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

/// <summary>search.json's one rule (SearchText.cs), which the sync and the bridge's --search-index both make a map's text with: the Python
/// sync's - each part cleaned as the sync cleans a title, then Python's str.lower() and str.split(). The sync's self-test runs every code point
/// through both programs.</summary>
static class SearchTextTests
{
    const char DotAbove = (char)0x307, Sigma = (char)0x3C3, FinalSigma = (char)0x3C2;

    public static void Run()
    {
        Check("search text: a map's name's words in lower case, its id, then its tags' words",
              SearchText.Of("FUN GAME AİM", "7", ["Classic", $"Arms{(char)0x1F}Race", "  "]) == $"fun game ai{DotAbove}m 7 classic arms race");
        Check("search text: Python's lower() - İ is i and a dot above, where .NET keeps it",
              SearchText.Lower("İ") == $"i{DotAbove}" && "İ".ToLowerInvariant() == "İ");
        Check("search text: Python's lower() - a capital sigma that ends a word is the final sigma, any other the small one",
              SearchText.Lower("ΟΔΥΣΣΕΑΣ: Σ'ΑΓΑΠΩ ΑΣ'Α Σ") == $"οδυ{Sigma}{Sigma}εα{FinalSigma}: {Sigma}'αγαπω α{Sigma}'α {Sigma}"
              && SearchText.Of("ΑΘΗΝΑΣ", "5", []) == $"αθηνα{FinalSigma} 5");
        Check("search text: Python's split() - its whitespace has U+001C-U+001F too; a zero width space is none",
              SearchText.Split($"a{(char)0x1C}b{(char)0xA0}c{(char)0x2028}d{(char)0x3000}e{(char)0x200B}f").SequenceEqual(["a", "b", "c", "d", $"e{(char)0x200B}f"]));
        Check("search text: cleaned as the sync cleans a title - a control character breaks a word, a bidi override goes",
              SearchText.Clean($"  a{(char)0}b{(char)0x202E}c\t\td  ") == "a bc d" && SearchText.Words($"x{(char)1}y{(char)0x2066}z") is ["x", "yz"]
              && SearchText.Clean(null) == "" && SearchText.Clean($"{(char)0x85}{(char)0x9F} ") == "");
        Check("search text: the description's words, the sync's only - its first 800 code points once lowered, so İ counts two",
              SearchText.Of("Big  Map!", "77", ["Arms Race", "Wingman"], "A <b>bold</b> map\nwith lines") == "big map! 77 arms race wingman a <b>bold</b> map with lines"
              && SearchText.Of("n", "1", [], new string('a', SearchText.DescriptionMax - 1) + "İb") == "n 1 " + new string('a', SearchText.DescriptionMax - 1) + "i"
              && SearchText.Of("n", "1", []) == "n 1");
    }
}
