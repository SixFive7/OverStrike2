using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace OverStrike2.Sync;

/// <summary>An SMTP answer that is not the one wanted.</summary>
public sealed class SmtpException(string message) : Exception(message);

/// <summary>A plain SMTP client for the problem mail: STARTTLS when the relay offers it, plain text, UTF-8.</summary>
public static class Smtp
{
    /// <summary>A header's text as RFC 2047 wants it: printable ASCII as it is, anything else as UTF-8 encoded words on folded lines, each short enough for
    /// a line of 76 behind "Subject: " and never splitting a character. A line break in the text cannot start a header of its own.</summary>
    public static string HeaderText(string text)
    {
        const int WordBytes = 39;                           // 52 characters of Base64 (no padding but at the end), 64 with its marks
        var clean = text.Replace('\r', ' ').Replace('\n', ' ');
        if (clean.All(c => c is >= ' ' and <= '~'))
            return clean;
        var words = new List<string>();
        var word = new List<byte>();
        Span<byte> one = stackalloc byte[4];
        foreach (var rune in clean.EnumerateRunes())
        {
            var n = rune.EncodeToUtf8(one);
            if (word.Count + n > WordBytes)
            {
                words.Add($"=?utf-8?B?{Convert.ToBase64String(word.ToArray())}?=");
                word.Clear();
            }
            word.AddRange(one[..n]);
        }
        words.Add($"=?utf-8?B?{Convert.ToBase64String(word.ToArray())}?=");
        return string.Join("\r\n ", words);
    }

    /// <summary>Plain-text mail through the SMTP relay (STARTTLS when it is offered, else plain; a TLS failure is tried again plain). Returns
    /// null when sent, else the reason.</summary>
    public static string? Send(SyncConfig cfg, string subject, string body)
    {
        if (cfg.SmtpHost.Length == 0 || cfg.MailFrom.Length == 0 || cfg.MailTo.Count == 0)
            return "no SMTP host, sender or recipient configured";
        var msg = $"From: {cfg.MailFrom}\r\nTo: {string.Join(", ", cfg.MailTo)}\r\nSubject: {HeaderText(subject)}\r\nDate: {DateTimeOffset.Now.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture)}{Offset(DateTimeOffset.Now.Offset)}\r\n"
                  + $"Message-ID: <{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Environment.ProcessId}.{Random.Shared.Next()}.os2-sync@{Environment.MachineName.ToLowerInvariant()}>\r\n"
                  + $"MIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\nX-Mailer: {SteamApi.UserAgent}\r\n\r\n{body.Replace("\n", "\r\n")}\r\n";
        var sender = Address(cfg.MailFrom);
        string? last = null;
        foreach (var first in new[] { true, false })
        {
            var tls = first;
            try
            {
                Session(cfg.SmtpHost, cfg.SmtpPort, ref tls, sender, cfg.MailTo.Select(Address).ToList(), msg);
                return null;
            }
            catch (Exception e) when (e is IOException or SocketException or SmtpException or System.Security.Authentication.AuthenticationException or TimeoutException
                                          or AggregateException)
            {
                last = $"{(e is AggregateException { InnerException: { } inner } ? inner : e).GetType().Name}: {(e is AggregateException { InnerException: { } i2 } ? i2 : e).Message}";
                if (!tls)
                    break;                           // plain already (no STARTTLS offered): no second try
            }
        }
        return last;
    }

    static string Offset(TimeSpan o) => (o < TimeSpan.Zero ? "-" : "+") + Math.Abs(o.Hours).ToString("00", CultureInfo.InvariantCulture) + Math.Abs(o.Minutes).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The address of "Name &lt;a@b&gt;", or the text itself.</summary>
    public static string Address(string s)
    {
        var a = s.IndexOf('<');
        var b = s.LastIndexOf('>');
        return a >= 0 && b > a ? s[(a + 1)..b].Trim() : s.Trim();
    }

    /// <summary>One SMTP session: EHLO, STARTTLS when asked for and offered (then EHLO again; `tls` false afterwards when it was not offered),
    /// MAIL FROM, RCPT TO, DATA (dot-stuffed), QUIT.</summary>
    public static void Session(string host, int port, ref bool tls, string from, List<string> to, string message, int timeoutMs = 60000)
    {
        using var tcp = new TcpClient { ReceiveTimeout = timeoutMs, SendTimeout = timeoutMs };
        if (!tcp.ConnectAsync(host, port).Wait(timeoutMs))
            throw new TimeoutException($"no connection to {host}:{port}");
        Stream stream = tcp.GetStream();
        var reader = new SmtpReader(stream);
        void Say(string line) => stream.Write(Encoding.UTF8.GetBytes(line + "\r\n"));
        (int Code, List<string> Lines) Expect(params int[] codes)
        {
            var reply = reader.Reply();
            if (!codes.Contains(reply.Code))
                throw new SmtpException($"({reply.Code}, '{string.Join(" ", reply.Lines)}')");
            return reply;
        }
        Expect(220);
        var hostname = Environment.MachineName.ToLowerInvariant();
        Say($"EHLO {hostname}");
        var ehlo = Expect(250);
        if (tls)
        {
            if (!ehlo.Lines.Any(l => l.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)))
                tls = false;
            else
            {
                Say("STARTTLS");
                Expect(220);
                var ssl = new SslStream(stream, false, (_, _, _, _) => true);   // a relay inside the deployment: its certificate is not checked
                ssl.AuthenticateAsClient(host);
                stream = ssl;
                reader = new SmtpReader(stream);
                Say($"EHLO {hostname}");
                Expect(250);
            }
        }
        Say($"MAIL FROM:<{from}>");
        Expect(250);
        foreach (var r in to)
        {
            Say($"RCPT TO:<{r}>");
            Expect(250, 251);
        }
        Say("DATA");
        Expect(354);
        var data = new StringBuilder();
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            data.Append(line.StartsWith('.') ? "." + line : line).Append("\r\n");
        if (message.EndsWith("\r\n", StringComparison.Ordinal))
            data.Length -= 2;                        // the last split part is empty: no extra line
        data.Append(".\r\n");
        stream.Write(Encoding.UTF8.GetBytes(data.ToString()));
        Expect(250);
        try
        {
            Say("QUIT");
            reader.Reply();
        }
        catch (Exception e) when (e is IOException or SmtpException)
        {
        }
    }

    sealed class SmtpReader(Stream s)
    {
        readonly List<byte> line = [];

        string Line()
        {
            line.Clear();
            while (true)
            {
                var b = s.ReadByte();
                if (b < 0)
                    throw new SmtpException("the connection closed");
                if (b == '\n')
                    return Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                line.Add((byte)b);
            }
        }

        public (int Code, List<string> Lines) Reply()
        {
            var lines = new List<string>();
            while (true)
            {
                var l = Line();
                if (l.Length < 3 || !int.TryParse(l[..3], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                    throw new SmtpException($"a reply that is no SMTP: {Text.Cut(l, 80)}");
                lines.Add(l.Length > 4 ? l[4..] : "");
                if (l.Length < 4 || l[3] != '-')
                    return (code, lines);
            }
        }
    }
}
