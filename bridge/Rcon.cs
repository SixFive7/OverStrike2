using System.Net.Sockets;
using Packets = OverStrike2.Core.Rcon;

/// <summary>
/// Minimal Source RCON client (https://developer.valvesoftware.com/wiki/Source_RCON_Protocol): one connection kept open, a reconnect, a map
/// command sent once, a reply matched by its id. The packets are core's (OverStrike2.Core.Rcon.Packet and Read), as for the server programs.
/// Not thread-safe: the bridge calls it from its single event-loop thread only.
/// </summary>
sealed class Rcon(string host, int port, string password)
{
    TcpClient? tcp;
    NetworkStream? stream;
    int nextId;

    /// <summary>Runs one console command; reconnects once on failure. Returns null when the server is unreachable.
    /// A map command that has gone out is never sent again: a map load drops RCON before the reply, and a second send loaded the map
    /// twice (#1919 awp_lorija) - its lost reply reads as empty, as a map command's reply usually is. A
    /// connection the server closed while idle is found before writing (Stale), so the retry is left for a failure before the send.</summary>
    public string? Exec(string command)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var sent = false;
            try
            {
                Connect();
                var id = Write(Packets.ExecCommand, command);
                sent = true;
                return Await(id);
            }
            catch (Exception e)
            {
                Close();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} rcon: {(e as AggregateException)?.InnerException?.Message ?? e.Message}");
                if (sent && ChangesMap(command)) { Console.WriteLine($"{DateTime.Now:HH:mm:ss} rcon: '{command}' went out - not sent twice"); return ""; }
                if (attempt == 0) Thread.Sleep(1000); // short backoff, then one retry
            }
        }
        return null;
    }

    /// <summary>A command that loads a map: sent once, whatever becomes of its reply.</summary>
    public static bool ChangesMap(string command) =>
        command.TrimStart().Split(' ', 2)[0].ToLowerInvariant() is "host_workshop_map" or "changelevel" or "map" or "ds_workshop_changelevel";

    /// <summary>The server has closed this connection (a map load, an idle timeout): readable with nothing to read.</summary>
    bool Stale()
    {
        try { return tcp?.Client is { } s && s.Poll(0, SelectMode.SelectRead) && s.Available == 0; }
        catch (Exception) { return true; }
    }

    void Connect()
    {
        if (tcp is { Connected: true } && !Stale()) return;
        Close();
        tcp = new TcpClient();
        tcp.ConnectAsync(host, port, new CancellationTokenSource(3000).Token).AsTask().GetAwaiter().GetResult();
        stream = tcp.GetStream();
        stream.ReadTimeout = stream.WriteTimeout = 5000;
        var id = Write(Packets.Auth, password);
        while (true) // the server may send an empty type-0 packet before the type-2 auth response
        {
            var (rid, type, _) = Read();
            if (type != Packets.AuthResponse) continue;
            if (rid != id) throw new InvalidOperationException("RCON authentication failed (wrong password?)");
            break;
        }
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} rcon: connected to {host}:{port}");
    }

    string Await(int id)
    {
        while (true) // skip late packets that belong to an earlier command
        {
            var (rid, _, text) = Read();
            if (rid == id) return text;
        }
    }

    int Write(int type, string body)
    {
        var id = ++nextId;
        stream!.Write(Packets.Packet(id, type, body));
        return id;
    }

    (int id, int type, string body) Read() => Packets.Read(stream!);

    void Close()
    {
        tcp?.Dispose();
        tcp = null;
        stream = null;
    }
}
