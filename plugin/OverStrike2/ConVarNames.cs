namespace OverStrike2;

/// <summary>The game convars the running build of CS2 has, asked of the build itself at every start: a setting Valve adds is watched from its
/// first start on, without a new convars.txt (Settings.cs). convars.txt stays the floor: whatever goes wrong here, the plugin watches what
/// that list names, as it always has, and says why in its log and in mapreset_check.
///
/// How: a convar of the game DLL has its name as a plain string in that DLL, so the candidates are read from the file (libserver.so) and the
/// engine is asked about each one by name - ConVar.Find, as the plugin always did for its list, then the flags and the type of what it found.
/// A string that is no convar is simply not found. Measured on CS2 1.41.9.4/14194: 31,235 strings in the file look like a name, and among them are
/// all 1,555 game convars cvarlist shows, each one a string of its own (no name is merged into the tail of a longer string: think_limit is
/// stored apart from vprof_think_limit). A convar whose name a build puts together at run time would be missed until convars.txt names it;
/// there are none today.
///
/// Why not the engine's own list (read on the same build, with CounterStrikeSharp 1.0.379, which has no native or API that lists convars):
///   - Walking its access indexes (new ConVar(i), NativeAPI.IsConvarValid): CCvar::GetConVarData has no bounds check and the count is not
///     exposed, so an index past the end takes whatever follows on the heap for a pointer, and CounterStrikeSharp follows it.
///   - ICvar::FindFirstConVar and FindNextConVar (VEngineCvar007) by virtual call: two slots of a table this plugin would have to hard-code.
///     They have not moved in the SDK's headers since 2025, though the table around them has changed size more than once, but a wrong slot
///     after an update is a crash at start that no try/catch stops.
///   - Running cvarlist from the plugin: con_logfile is no convar on this build, so the output could only be read from the log file a launch
///     option opens, as 3,600 lines in the file the restart watcher reads, or through a native logging hook.
/// A crash at a server start is far worse than a setting not watched, so this route asks only by name and reads only what the engine hands back.
///
/// No CounterStrikeSharp type here: ../OverStrike2.SelfTest compiles this file and fakes the engine.</summary>
static class ConVarNames
{
    /// <summary>FCVAR_GAMEDLL: defined by the game DLL, "sv" in cvarlist.</summary>
    public const ulong GameDll = 1ul << 2;

    /// <summary>What cvarlist leaves out (its test on every convar, read in libtier0.so: flags &amp; 0x412): FCVAR_DEVELOPMENTONLY, FCVAR_HIDDEN
    /// and FCVAR_REFERENCE. ConVar.Find can return some of these; a list made from cvarlist never named them, so they stay out here too and
    /// the two ways give the same set.</summary>
    public const ulong Hidden = 1ul << 1 | 1ul << 4 | 1ul << 10;

    /// <summary>The last convar type CounterStrikeSharp's ConVarType names (QAngle). Reading the value of a convar of a type it does not know
    /// runs through a table sized at its build, so a new name of a newer type is left out rather than read.</summary>
    public const short LastKnownType = 14;

    /// <summary>What a string must be to be asked about. The longest convar name today has 62 characters, the shortest 2.</summary>
    public const int MinName = 2, MaxName = 96;

    const int ReadSize = 1 << 20;

    /// <summary>What the engine says about a name it knows as a convar.</summary>
    public readonly record struct Answer(ulong Flags, short Type);

    /// <param name="New">The game convars this build has that the list lacks, sorted; empty when the answer was refused.</param>
    /// <param name="Listed">The list's names.</param>
    /// <param name="Strings">The strings in the file that look like a name.</param>
    /// <param name="Seen">The list's names among those strings.</param>
    /// <param name="OddType">Game convars the list lacks, left out for a type this plugin does not know.</param>
    /// <param name="Refused">Why nothing from the file is used, or null.</param>
    public sealed record Found(IReadOnlyList<string> New, int Listed, int Strings, int Seen, int OddType, string? Refused)
    {
        /// <summary>The line for the log and for mapreset_check.</summary>
        public string Describe(long milliseconds) => Refused != null
            ? $"convars.txt only ({Listed} names), the game DLL not used: {Refused}"
            : $"convars.txt ({Listed} names) and the game DLL ({Strings} strings, {Seen} of the names among them): " +
              $"{New.Count} game convars only this build has{(New.Count > 0 ? ": " + string.Join(", ", New) : "")}" +
              $"{(OddType > 0 ? $"; {OddType} of an unknown type left out" : "")} ({milliseconds} ms)";
    }

    public static string BinaryPath(string gameDirectory, bool windows) =>
        Path.Combine(gameDirectory, "csgo", "bin", windows ? "win64" : "linuxsteamrt64", windows ? "server.dll" : "libserver.so");

    /// <summary>The names of a list like convars.txt: one per line.</summary>
    public static List<string> ParseList(TextReader reader)
    {
        var names = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length > 0) names.Add(line);
        }
        return names;
    }

    static bool IsNameByte(byte b, bool first)
    {
        var c = (char)b;
        return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || (!first && c is >= '0' and <= '9' or '.' or '-');
    }

    /// <summary>Every string of the file that could be a convar's name: bytes between two NULs (or from the file's start), all of them name
    /// characters, of a fitting length. Compared without case, as the engine finds convars. The file is read in pieces, not held.</summary>
    public static HashSet<string> Strings(Stream binary)
    {
        var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[ReadSize];
        var run = new char[MaxName];
        var length = 0;
        var clean = true; // the bytes since the last NUL could still be a name
        int read;
        while ((read = binary.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                if (b == 0)
                {
                    if (clean && length >= MinName) strings.Add(new string(run, 0, length));
                    length = 0;
                    clean = true;
                }
                else if (clean)
                {
                    if (length < MaxName && IsNameByte(b, length == 0)) run[length++] = (char)b;
                    else clean = false;
                }
            }
        return strings;
    }

    /// <summary>The strings the list does not name are asked about; what the engine calls a visible game convar of a known type is new. Two
    /// sanity checks refuse the whole answer, because then this is not the file or the engine this was written for: less than half of the
    /// list's names in the file, or more new game convars than the list has names.</summary>
    public static Found Discover(IReadOnlyCollection<string> list, IReadOnlyCollection<string> strings, Func<string, Answer?> ask)
    {
        var listed = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        var seen = strings.Count(listed.Contains);
        Found No(string why) => new([], listed.Count, strings.Count, seen, 0, why);
        if (strings.Count == 0) return No("no string in it looks like a name");
        if (seen * 2 < listed.Count) return No($"only {seen} of the {listed.Count} names are in it");
        var found = new List<string>();
        var oddType = 0;
        foreach (var name in strings)
        {
            if (listed.Contains(name) || ask(name) is not { } answer) continue;
            if ((answer.Flags & GameDll) == 0 || (answer.Flags & Hidden) != 0) continue;
            if (answer.Type is < 0 or > LastKnownType) { oddType++; continue; }
            found.Add(name);
        }
        if (listed.Count > 0 && found.Count > listed.Count) return No($"{found.Count} new game convars, more than the list has names");
        found.Sort(StringComparer.Ordinal);
        return new(found, listed.Count, strings.Count, seen, oddType, null);
    }

    /// <summary>The whole question, which never throws: a file that cannot be opened or read, or an engine call that fails, leaves the list as it is.</summary>
    public static Found Ask(Func<Stream> open, IReadOnlyCollection<string> list, Func<string, Answer?> ask)
    {
        try
        {
            using var binary = open();
            return Discover(list, Strings(binary), ask);
        }
        catch (Exception e)
        {
            return new([], list.Count, 0, 0, 0, e.Message);
        }
    }
}
