// --selftest, the RCON client against a stand-in server on localhost: a map command that has gone out is never sent twice -
// a map load drops RCON before the reply, and the old retry loaded #1919 awp_lorija twice - while a query still gets
// its one retry, and a connection the server closed while idle is found before writing.
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

static partial class Program
{
    static void RconSelfTest(Action<string, bool> Check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = new ConcurrentQueue<string>();
        var dropOnce = new ConcurrentDictionary<string, bool>(); // commands whose first arrival drops the connection without a reply
        var idleClose = new ConcurrentQueue<TcpClient>();
        var stop = new CancellationTokenSource();

        static (int Id, int Type, string Body)? ReadPacket(NetworkStream s)
        {
            var head = new byte[4];
            try { s.ReadExactly(head); } catch (Exception) { return null; }
            var pkt = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
            s.ReadExactly(pkt);
            return (BinaryPrimitives.ReadInt32LittleEndian(pkt), BinaryPrimitives.ReadInt32LittleEndian(pkt.AsSpan(4)), Encoding.UTF8.GetString(pkt, 8, pkt.Length - 10));
        }

        static void WritePacket(NetworkStream s, int id, int type, string body)
        {
            var payload = Encoding.UTF8.GetBytes(body);
            var buf = new byte[14 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(buf, buf.Length - 4);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), id);
            BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), type);
            payload.CopyTo(buf, 12);
            s.Write(buf);
        }

        void Serve(TcpClient c)
        {
            using var conn = c;
            var s = c.GetStream();
            if (ReadPacket(s) is not { Type: 3 } auth) return;
            WritePacket(s, auth.Id, 0, "");   // what CS2 sends first
            WritePacket(s, auth.Id, 2, "");   // the auth answer
            idleClose.Enqueue(c);
            while (ReadPacket(s) is { } cmd)
            {
                received.Enqueue(cmd.Body);
                if (dropOnce.TryRemove(cmd.Body, out _)) return; // a map load: the connection goes before any reply
                WritePacket(s, cmd.Id, 0, "ok " + cmd.Body);
            }
        }

        var acceptor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try { var c = await listener.AcceptTcpClientAsync(stop.Token); _ = Task.Run(() => Serve(c)); }
                catch (Exception) { return; }
            }
        });

        var rc = new Rcon("127.0.0.1", port, "selftest");
        int Count(string body) => received.Count(x => x == body);
        try
        {
            var first = rc.Exec("status");
            dropOnce["host_workshop_map 3255717952"] = true;
            var map = rc.Exec("host_workshop_map 3255717952");
            var after = rc.Exec("os_mode armsrace");
            Check("RCON: a map command whose reply is lost with the connection (a map load) goes out once, not twice, and reads as empty; the next command reconnects",
                first == "ok status" && map == "" && Count("host_workshop_map 3255717952") == 1 && after == "ok os_mode armsrace");

            dropOnce["game_type"] = true;
            var query = rc.Exec("game_type");
            Check("RCON: a query whose reply is lost is sent once more and answered", query == "ok game_type" && Count("game_type") == 2);

            while (idleClose.TryDequeue(out var c)) try { c.Client.Shutdown(SocketShutdown.Both); } catch (Exception) { } // the server closes every idle connection (some are gone already)
            Thread.Sleep(200);
            var changed = rc.Exec("changelevel de_dust2");
            Check("RCON: a connection the server closed while idle is found before writing, so a map command on it still goes out, once",
                changed == "ok changelevel de_dust2" && Count("changelevel de_dust2") == 1);
            Check("RCON: map commands are the ones never sent twice", Rcon.ChangesMap("host_workshop_map 1") && Rcon.ChangesMap(" changelevel ar_baggage")
                && Rcon.ChangesMap("map de_dust2") && !Rcon.ChangesMap("os_mode deathmatch;game_type 1;game_mode 2") && !Rcon.ChangesMap("status"));
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
