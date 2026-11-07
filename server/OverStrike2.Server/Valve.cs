using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OverStrike2.Core;

namespace OverStrike2.Server;

/// <summary>THE VERSION TRAP. Valve's UpToDateCheck does a numeric >= against required_version and reports "up to date" for almost any
/// number (measured: version=14186 -> up_to_date false, required 14188; 14188 -> true; 2000919, steam.inf's ServerVersion of a build
/// known to be stale -> true). The only correct input is steam.inf's dotted PatchVersion with the dots removed (1.41.8.8 -> 14188):
/// core's SteamInf.PatchVersion reads that field and nothing else, and VersionInt rejects anything without a dot.</summary>
public static partial class Versions
{
    [GeneratedRegex(@"^[0-9]+(?:\.[0-9]+)+$")] private static partial Regex Dotted();   // at least one dot: rejects ServerVersion

    public const string ValveUrl = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/?appid=730&version={0}";

    public static bool IsDotted(string? s) => s != null && Dotted().IsMatch(s);

    /// <summary>1.41.8.8 -> 14188; null for anything not dotted or outside a plausible range.</summary>
    public static int? VersionInt(string? dotted)
    {
        if (!IsDotted(dotted))
            return null;
        return long.TryParse(dotted!.Replace(".", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v is >= 1000 and <= 99999999
            ? (int)v : null;
    }


    /// <summary>An UpToDateCheck reply for build `vint`: ("current", null), ("stale", required) or ("unknown", why). Every field is
    /// type-checked; required_version must be strictly newer than ours.</summary>
    public static (string Answer, int? Required, string? Why) ValveVerdict(string raw, int vint)
    {
        JsonElement doc;
        try
        {
            doc = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException e)
        {
            return ("unknown", null, $"response is not JSON ({e.Message})");
        }
        if (doc.ValueKind != JsonValueKind.Object || !doc.TryGetProperty("response", out var body) || body.ValueKind != JsonValueKind.Object)
            return ("unknown", null, "no response object");
        if (!body.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            return ("unknown", null, $"success is not true ({Show(body, "success")})");
        body.TryGetProperty("up_to_date", out var upToDate);
        if (upToDate.ValueKind == JsonValueKind.True)
            return ("current", null, null);
        if (upToDate.ValueKind != JsonValueKind.False)
            return ("unknown", null, $"up_to_date is not a boolean ({Show(body, "up_to_date")})");
        if (!body.TryGetProperty("required_version", out var req) || req.ValueKind != JsonValueKind.Number || !req.TryGetInt32(out var required))
            return ("unknown", null, $"required_version is absent or not an integer ({Show(body, "required_version")})");
        if (required <= vint)
            return ("unknown", null, $"required_version {required} is not newer than the local {vint}");
        return ("stale", required, null);
    }

    static string Show(JsonElement body, string key) => body.TryGetProperty(key, out var v) ? v.GetRawText() : "None";
}

/// <summary>Valve's UpToDateCheck for a build, through this program's one Web API door.</summary>
public interface IValve
{
    (string Answer, int? Required, string? Why) Check(int vint);
}

/// <summary>Valve's UpToDateCheck, through the one door. fetch: a test's stand-in for the HTTP call (status, body).</summary>
public sealed class ValveApi(WebApiDoor door, Func<int, (int Status, string Body)>? fetch = null) : IValve
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public (string Answer, int? Required, string? Why) Check(int vint) => door.Call(() =>
    {
        try
        {
            var (status, body) = (fetch ?? Fetch)(vint);
            return status != 200 ? ("unknown", (int?)null, $"HTTP {status}") : Versions.ValveVerdict(body, vint);
        }
        catch (Exception e)   // the HTTP stack throws a wide family; none of them may stop the loop
        {
            return ("unknown", null, $"{e.GetType().Name}: {e.Message}");
        }
    });

    static (int, string) Fetch(int vint)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(CultureInfo.InvariantCulture, Versions.ValveUrl, vint));
        req.Headers.UserAgent.ParseAdd(HttpBridge.UserAgent);
        using var resp = Http.Send(req);
        if (resp.StatusCode != HttpStatusCode.OK)
            return ((int)resp.StatusCode, "");
        using var s = resp.Content.ReadAsStream();
        var buf = new byte[65536];
        int n = 0, got;
        while (n < buf.Length && (got = s.Read(buf, n, buf.Length - n)) > 0)
            n += got;
        return (200, Encoding.UTF8.GetString(buf, 0, n));
    }
}
