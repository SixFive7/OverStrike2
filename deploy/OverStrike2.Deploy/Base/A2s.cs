using System.Globalization;

namespace OverStrike2.Deploy;

/// <summary>What one A2S_INFO answer says: the server's name and map, humans (players minus bots) and the VAC flag (1 or 0).</summary>
sealed record A2sAnswer(string Name, string Map, int Humans, int Vac) : ITuple
{
    public int Length => 4;
    public object? this[int index] => index switch { 0 => Name, 1 => Map, 2 => Humans, _ => Vac };
}

/// <summary>Is anybody playing? core's one A2S_INFO (OverStrike2.Core.A2s, which the bridge and the server program ask too), read the three
/// ways the commands need it. No answer is never "empty".</summary>
static class A2s
{
    /// <summary>host:port split at its last colon; null when the port is no number.</summary>
    public static (string Host, int Port)? Split(string? address)
    {
        address ??= "";
        var colon = address.LastIndexOf(':');
        var host = colon < 0 ? "" : address[..colon];
        return int.TryParse(Py.Strip(colon < 0 ? address : address[(colon + 1)..]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? (host, p) : null;
    }

    /// <summary>One A2S_INFO, answering a challenge if the server asks for one; null when nothing came back in time.</summary>
    public static A2sAnswer? Ask(string host, int port, double timeoutSeconds)
    {
        if (host.Length == 0)
            return null;
        try
        {
            var i = Core.A2s.Ask(host, port, TimeSpan.FromSeconds(timeoutSeconds));
            return i is null ? null : new A2sAnswer(i.Name, i.Map, i.Humans, i.Vac ? 1 : 0);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Players minus bots on the server at this address; null when it does not answer.</summary>
    public static Func<string, int?> Humans = address => Split(address) is var (host, port) ? Ask(host, port, 3)?.Humans : null;

    /// <summary>(humans, the VAC flag) on the server at this address; null when it does not answer.</summary>
    public static (int Humans, int Vac)? Info(string? address) =>
        Split(address) is var (host, port) && Ask(host, port, 3) is { } a ? (a.Humans, a.Vac) : null;

    /// <summary>(name, map, humans, VAC) of the server; null when it does not answer.</summary>
    public static A2sAnswer? Full(string host, int port, double timeout = 2.0) => Ask(host, port, timeout);
}

/// <summary>What every command that ships shows before it changes anything: who is on, and who holds the server lock.</summary>
static class Preflight
{
    /// <summary>The humans line (when the config names the server's address), with what this command's change does to them; then the lock
    /// line, when somebody holds the lock.</summary>
    public static void Show(IConfig cfg, string? consequence)
    {
        var address = cfg.Str("server.address");
        if (consequence is not null && address.Length > 0)
        {
            var on = A2s.Humans(address);
            Out.Log($"humans on the server: {(on is null ? "unknown (no A2S answer)" : on.Value.ToString(CultureInfo.InvariantCulture))}{consequence}");
        }
        var note = Repo.LockNote(cfg);
        if (!string.IsNullOrEmpty(note))
            Out.Log("server lock: " + Repo.Oneline(note));
    }

    /// <summary>The targets whose server copy is neither the checkout's file nor any earlier commit's: it was changed on the server - DRIFT.
    /// files: (the file in this checkout, its path in the repository, the server copy). A target the server lacks is no drift.</summary>
    public static List<string> Drifted(IEnumerable<(string Local, string Rel, string Target)> files)
    {
        var output = new List<string>();
        foreach (var (local, rel, target) in files)
        {
            if (!File.Exists(target))
                continue;
            var live = File.ReadAllBytes(target);
            if (!live.AsSpan().SequenceEqual(File.ReadAllBytes(local)) && Repo.CommittedBefore(rel, live, 50) is null)
                output.Add(target);
        }
        return output;
    }

    /// <summary>The refusal when something drifted, unless --force.</summary>
    public static void RefuseDrift(IReadOnlyCollection<string> drifted, bool force)
    {
        if (drifted.Count > 0 && !force)
            throw Out.Fail($"DRIFT: changed on the server, and no commit holds that copy: {string.Join(", ", drifted)} - take the server's changes into the "
                           + "repository first, or --force after you looked");
    }
}
