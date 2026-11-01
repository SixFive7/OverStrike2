using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Minimal Source RCON client (https://developer.valvesoftware.com/wiki/Source_RCON_Protocol).
/// Packet: int32 length, int32 id, int32 type, body, NUL, NUL. Types: 3 = auth, 2 = exec, 0 = response, 2 = auth response.
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
                var id = Write(2, command);
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
        var id = Write(3, password);
        while (true) // the server may send an empty type-0 packet before the type-2 auth response
        {
            var (rid, type, _) = Read();
            if (type != 2) continue;
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
        var payload = Encoding.UTF8.GetBytes(body);
        var buf = new byte[14 + payload.Length]; // 4 id + 4 type + body + 2 NUL, plus the 4-byte length itself
        BinaryPrimitives.WriteInt32LittleEndian(buf, buf.Length - 4);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), type);
        payload.CopyTo(buf, 12);
        stream!.Write(buf);
        return id;
    }

    (int id, int type, string body) Read()
    {
        var head = new byte[4];
        stream!.ReadExactly(head);
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 10 || len > 1 << 20) throw new InvalidDataException($"bad RCON packet length {len}");
        var pkt = new byte[len];
        stream.ReadExactly(pkt);
        return (BinaryPrimitives.ReadInt32LittleEndian(pkt), BinaryPrimitives.ReadInt32LittleEndian(pkt.AsSpan(4)), Encoding.UTF8.GetString(pkt, 8, len - 10));
    }

    void Close()
    {
        tcp?.Dispose();
        tcp = null;
        stream = null;
    }
}
