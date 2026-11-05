using System.Net;
using System.Net.Sockets;
using System.Text;
using static OverStrike2.Core.SelfTest.T;

namespace OverStrike2.Core.SelfTest;

static class NetTests
{
    /// <summary>An A2S_INFO reply as a CS2 server sends it.</summary>
    public static byte[] Info(string map, int players, int bots, bool vac, string version = "1.41.9.2")
    {
        var b = new List<byte> { 0xFF, 0xFF, 0xFF, 0xFF, (byte)'I', 0x11 };
        foreach (var s in new[] { "Over Strike 2 test", map, "csgo", "Counter-Strike 2" })
        {
            b.AddRange(Encoding.UTF8.GetBytes(s));
            b.Add(0);
        }
        b.AddRange([0xDA, 0x02, (byte)players, 32, (byte)bots, (byte)'d', (byte)'l', 0, (byte)(vac ? 1 : 0)]);
        b.AddRange(Encoding.ASCII.GetBytes(version));
        b.Add(0);
        return [.. b];
    }

    public static void Run()
    {
        var info = A2s.Parse(Info("ar_shoots", 5, 2, true));
        Check("a2s: a reply - map, players, bots, humans, VAC, version", info is { Map: "ar_shoots", Players: 5, Bots: 2, Humans: 3, Vac: true, Version: "1.41.9.2", MaxPlayers: 32 });
        Check("a2s: more bots than players is 0 humans, never fewer", A2s.Parse(Info("x", 1, 3, false))?.Humans == 0);
        Check("a2s: not an info reply, or cut short, is null", A2s.Parse([0xFF, 0xFF, 0xFF, 0xFF, (byte)'A', 1, 2, 3, 4]) is null
              && A2s.Parse(Info("ar_shoots", 1, 0, false)[..20]) is null && A2s.Parse([]) is null);

        // a stand-in server that asks for a challenge first, as CS2 does
        using (var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var asked = new List<int>();
            var server = Task.Run(() =>
            {
                IPEndPoint? from = null;
                var q = udp.Receive(ref from);
                asked.Add(q.Length);
                udp.Send([0xFF, 0xFF, 0xFF, 0xFF, (byte)'A', 9, 8, 7, 6], 9, from);
                q = udp.Receive(ref from);
                asked.Add(q.Length);
                var ok = q.Length == 29 && q[^4] == 9 && q[^1] == 6;
                var reply = Info(ok ? "with the challenge" : "without", 0, 0, true);
                udp.Send(reply, reply.Length, from);
            });
            var got = A2s.Ask("127.0.0.1", port, TimeSpan.FromSeconds(3));
            server.Wait(3000);
            Check("a2s: Ask answers the challenge with it appended, then reads the reply", got?.Map == "with the challenge" && asked.SequenceEqual([25, 29]));
        }
        using (var silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            var port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;
            Check("a2s: no answer is null, never empty", A2s.Ask("127.0.0.1", port, TimeSpan.FromMilliseconds(300)) is null);
        }

        // RCON
        var packet = Rcon.Packet(7, Rcon.ExecCommand, "status");
        var (id, type, body) = Rcon.Read(new MemoryStream(packet));
        Check("rcon: a packet - size, id, type, body, two nulls - reads back", packet.Length == 4 + 4 + 4 + 6 + 2 && id == 7 && type == Rcon.ExecCommand && body == "status");
        Check("rcon: an implausible size is an error", () =>
        {
            try
            {
                Rcon.Read(new MemoryStream([0x01, 0x00, 0x01, 0x00, 0, 0, 0, 0]));
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        });
        using (var fake = new FakeRcon("good"))
        {
            using var r = Rcon.Open("127.0.0.1", fake.Port, "good", TimeSpan.FromSeconds(3));
            var reply = r?.Run("status");
            Check("rcon: a session - authenticated after the empty value CS2 sends first, a command's reply", r != null && reply == "echo:status");
            Check("rcon: a command without waiting for a reply", r?.Run("quit", reply: false) is null);
        }
        using (var fake = new FakeRcon("good"))
            Check("rcon: a refused password is no session", Rcon.Open("127.0.0.1", fake.Port, "bad", TimeSpan.FromSeconds(3)) is null);
        using (var fake = new FakeRcon("good"))
            Check("rcon: Alive - an engine that answers the authentication, refused or not", Rcon.Alive("127.0.0.1", fake.Port, "bad", TimeSpan.FromSeconds(3)));
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        Check("rcon: Alive - nothing listening is not alive", !Rcon.Alive("127.0.0.1", closedPort, "x", TimeSpan.FromMilliseconds(500)));
    }

    /// <summary>A stand-in RCON server: one connection at a time; an empty response value before the auth response, as CS2 sends.</summary>
    sealed class FakeRcon : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        public int Port { get; }

        public FakeRcon(string password)
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var client = await listener.AcceptTcpClientAsync(stop.Token);
                        var s = client.GetStream();
                        var (id, _, pw) = Rcon.Read(s);
                        s.Write(Rcon.Packet(id, Rcon.ResponseValue, ""));
                        s.Write(Rcon.Packet(pw == password ? id : -1, Rcon.AuthResponse, ""));
                        if (pw != password)
                            continue;
                        try
                        {
                            while (true)
                            {
                                var (cid, _, cmd) = Rcon.Read(s);
                                if (cmd != "quit")
                                    s.Write(Rcon.Packet(cid, Rcon.ResponseValue, "echo:" + cmd));
                            }
                        }
                        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException)
                        {
                        }
                    }
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
