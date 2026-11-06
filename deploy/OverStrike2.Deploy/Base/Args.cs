namespace OverStrike2.Deploy;

/// <summary>A command's arguments, read the way the Python scripts' argparse read them: flags and options anywhere, "--name=value" or
/// "--name value", positionals in order. A mistake ends the command with exit 2 and the usage, as argparse did.</summary>
sealed class Args
{
    readonly string usage;
    readonly HashSet<string> flags;
    readonly HashSet<string> options;
    readonly HashSet<string> seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    readonly List<(string Text, bool Positional)> left = [];   // what no flag or option took, in its order

    public Args(string[] args, string usage, IEnumerable<string>? flags = null, IEnumerable<string>? options = null)
    {
        this.usage = usage;
        this.flags = new HashSet<string>(flags ?? [], StringComparer.Ordinal);
        this.options = new HashSet<string>(options ?? [], StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "-h" or "--help")
            {
                Console.Out.WriteLine("usage: os2-deploy " + usage);
                throw new Exit(0);
            }
            if (a == "--")
            {
                left.AddRange(args[(i + 1)..].Select(x => (x, true)));
                break;
            }
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var eq = a.IndexOf('=');
                var name = eq < 0 ? a : a[..eq];
                if (this.flags.Contains(name))
                {
                    if (eq >= 0)
                        throw Mistake($"argument {name}: ignored explicit argument '{a[(eq + 1)..]}'");
                    seen.Add(name);
                    continue;
                }
                if (this.options.Contains(name))
                {
                    if (eq >= 0)
                        values[name] = a[(eq + 1)..];
                    else if (i + 1 < args.Length)
                        values[name] = args[++i];
                    else
                        throw Mistake($"argument {name}: expected one argument");
                    seen.Add(name);
                    continue;
                }
                left.Add((a, false));
                continue;
            }
            left.Add((a, true));
        }
    }

    Exit Mistake(string what)
    {
        Console.Error.WriteLine("usage: os2-deploy " + usage);
        Console.Error.WriteLine("error: " + what);
        return new Exit(2);
    }

    public bool Flag(string name) => seen.Contains(name);

    public string? Opt(string name) => values.TryGetValue(name, out var v) ? v : null;

    public int IntOpt(string name, int dflt)
    {
        var v = Opt(name);
        if (v is null)
            return dflt;
        if (!int.TryParse(v, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n))
            throw Mistake($"argument {name}: invalid int value: '{v}'");
        return n;
    }

    /// <summary>The next positional, which must be one of the choices.</summary>
    public string Choice(IReadOnlyList<string> choices, string what = "command")
    {
        var v = Positional() ?? throw Mistake($"the following arguments are required: {what}");
        if (!choices.Contains(v))
            throw Mistake($"argument {what}: invalid choice: '{v}' (choose from {string.Join(", ", choices.Select(c => "'" + c + "'"))})");
        return v;
    }

    /// <summary>The next positional, or null when there is none.</summary>
    public string? Positional()
    {
        var at = left.FindIndex(x => x.Positional);
        if (at < 0)
            return null;
        var v = left[at].Text;
        left.RemoveAt(at);
        return v;
    }

    /// <summary>What is left, in its order: positionals not taken and options this command does not know (for commands that hand them on).</summary>
    public List<string> Rest()
    {
        var rest = left.Select(x => x.Text).ToList();
        left.Clear();
        return rest;
    }

    /// <summary>Nothing may be left over.</summary>
    public void Done()
    {
        if (left.Count > 0)
            throw Mistake("unrecognized arguments: " + string.Join(' ', left.Select(x => x.Text)));
    }
}
