using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OverStrike2.Core;

/// <summary>What an A2S_INFO reply says about a game server.</summary>
public sealed record A2sInfo(string Name, string Map, string Folder, string Game, int Players, int MaxPlayers, int Bots, bool Vac, string Version)
{
    /// <summary>Players minus bots, never below 0.</summary>
    public int Humans => Math.Max(0, Players - Bots);
}

/// <summary>The Source query protocol's A2S_INFO: a UDP question every game server answers. No answer is never "empty".</summary>
public static class A2s
{
    static readonly byte[] Query = [0xFF, 0xFF, 0xFF, 0xFF, (byte)'T', .. Encoding.ASCII.GetBytes("Source Engine Query"), 0];

    /// <summary>An A2S_INFO reply, or null when the bytes are not one.</summary>
    public static A2sInfo? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 6 || data[4] != (byte)'I')
                return null;
            var offset = 6;
            var fields = new string[4];
            for (var i = 0; i < 4; i++)              // name, map, folder, game
            {
                var end = data[offset..].IndexOf((byte)0);
                if (end < 0)
                    return null;
                fields[i] = Encoding.UTF8.GetString(data.Slice(offset, end));
                offset += end + 1;
            }
            offset += 2;                             // the app id
            int players = data[offset], max = data[offset + 1], bots = data[offset + 2];
            var vac = data[offset + 6] == 1;         // server type, environment, visibility, then VAC
            offset += 7;
            var vend = data[offset..].IndexOf((byte)0);
            if (vend < 0)
                return null;
            return new A2sInfo(fields[0], fields[1], fields[2], fields[3], players, max, bots, vac, Encoding.UTF8.GetString(data.Slice(offset, vend)));
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>One A2S_INFO, answering a challenge if the server asks for one; null when nothing (or no reply) came back in time.</summary>
    public static A2sInfo? Ask(string host, int port, TimeSpan timeout)
    {
        try
        {
            using var udp = new UdpClient(host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
            udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
            udp.Connect(host, port);
            udp.Send(Query, Query.Length);
            IPEndPoint? from = null;
            var data = udp.Receive(ref from);
            if (data.Length >= 9 && data[4] == (byte)'A')     // S2C_CHALLENGE: ask again with it appended
            {
                var again = new byte[Query.Length + 4];
                Query.CopyTo(again, 0);
                Array.Copy(data, 5, again, Query.Length, 4);
                udp.Send(again, again.Length);
                data = udp.Receive(ref from);
            }
            return Parse(data);
        }
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>The Source RCON protocol over TCP: an authenticated session that runs console commands.</summary>
public sealed class Rcon : IDisposable
{
    public const int Auth = 3, ExecCommand = 2, AuthResponse = 2, ResponseValue = 0;

    readonly TcpClient tcp;
    readonly NetworkStream stream;
    int nextId = 10;

    Rcon(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    /// <summary>One packet: size, id, type, the body and two nulls - little-endian.</summary>
    public static byte[] Packet(int id, int type, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var buf = new byte[4 + 8 + payload.Length + 2];
        BinaryPrimitives.WriteInt32LittleEndian(buf, 8 + payload.Length + 2);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), type);
        payload.CopyTo(buf, 12);
        return buf;
    }

    /// <summary>The next packet from a stream: (id, type, body). Throws IOException for a closed stream or an implausible size.</summary>
    public static (int Id, int Type, string Body) Read(Stream s)
    {
        Span<byte> head = stackalloc byte[4];
        s.ReadExactly(head);
        var size = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (size is < 10 or > 65536)
            throw new IOException($"an implausible RCON packet size {size}");
        var buf = new byte[size];
        s.ReadExactly(buf);
        return (BinaryPrimitives.ReadInt32LittleEndian(buf), BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(4)),
                Encoding.UTF8.GetString(buf, 8, size - 10));
    }

    /// <summary>A session, authenticated; null when the server refused the password. Throws IOException or SocketException when it
    /// cannot be reached.</summary>
    public static Rcon? Open(string host, int port, string password, TimeSpan timeout)
    {
        var tcp = new TcpClient { ReceiveTimeout = (int)timeout.TotalMilliseconds, SendTimeout = (int)timeout.TotalMilliseconds };
        try
        {
            if (!tcp.ConnectAsync(host, port).Wait(timeout))
                throw new IOException($"no connection to {host}:{port} within {timeout.TotalSeconds:0} s");
            var r = new Rcon(tcp);
            r.stream.Write(Packet(1, Auth, password));
            while (true)
            {
                var (id, type, _) = Read(r.stream);
                if (type != AuthResponse)
                    continue;                        // the empty response value some servers send first
                if (id == -1)
                {
                    r.Dispose();
                    return null;
                }
                return r;
            }
        }
        catch (AggregateException e) when (e.InnerException is SocketException se)
        {
            tcp.Dispose();
            throw se;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Run one command; its reply, or null when no reply was asked for. Throws IOException when the reply does not come.</summary>
    public string? Run(string command, bool reply = true)
    {
        stream.Write(Packet(nextId++, ExecCommand, command));
        return reply ? Read(stream).Body : null;
    }

    /// <summary>Does anything answer RCON's authentication at all - accepted or refused, both come from a running engine loop? Sends no
    /// command, so nothing is echoed into the console or the log stream.</summary>
    public static bool Alive(string host, int port, string password, TimeSpan timeout)
    {
        try
        {
            using var tcp = new TcpClient { ReceiveTimeout = (int)timeout.TotalMilliseconds, SendTimeout = (int)timeout.TotalMilliseconds };
            if (!tcp.ConnectAsync(host, port).Wait(timeout))
                return false;
            var s = tcp.GetStream();
            s.Write(Packet(1, Auth, password));
            Read(s);
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        stream.Dispose();
        tcp.Dispose();
    }
}
