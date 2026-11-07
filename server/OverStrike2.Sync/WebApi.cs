using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OverStrike2.Core;

namespace OverStrike2.Sync;

/// <summary>A limit or a refusal: no more Web API calls this run (a cap) or today (Steam's push-back).</summary>
public sealed class StopApiException(string message) : Exception(message);

/// <summary>One call failed and was given up; the run goes on.</summary>
public class ApiErrorException(string message) : Exception(message);

/// <summary>A 4xx other than 401/403/429: asking again would get the same answer, so it is not retried.</summary>
public sealed class ApiRefusedException(string message) : ApiErrorException(message);

/// <summary>One request to the Web API as it goes out: a GET with its query, or a POST with its form.</summary>
public sealed record ApiRequest(string Method, string Url, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>Its answer: the HTTP status and the body. A network failure is an exception (IOException, HttpRequestException, a timeout).</summary>
public sealed record ApiAnswer(int Status, byte[] Body);

/// <summary>What the steps ask the Workshop through: the Web API, or a test's stand-in.</summary>
public interface IWorkshopApi
{
    Dictionary<string, int> ByApi { get; }
    (List<JsonObject> Items, string NextCursor, long Total) QueryFiles(IReadOnlyList<string> tags, string cursor, int perPage);
    Dictionary<string, JsonArray> Votes(IReadOnlyList<string> ids);
    (List<string> Ids, string NextCursor, long Total) Trend(string tag, string cursor, int days, int perPage = 100);
    Dictionary<string, JsonObject> Details(IReadOnlyList<string> ids);
}

/// <summary>Every Web API call this tool makes, persisted BEFORE it is sent (&lt;state&gt;/api-ledger.json): the rolling 24 h cap, the count
/// per day, the day's stop - so a crash never loses one, and a restart cannot resume a stop. An unreadable ledger means no calls at all.
/// The per-run cap is SteamApi's.</summary>
public sealed class ApiLedger(string path, int dayCap, SyncClock clock)
{
    public const double Day = 86400;
    public string Path { get; } = path;
    public int DayCap { get; } = Math.Min(dayCap, SyncConfig.HardApiDayCap);
    public JsonObject? Data { get; set; }
    public string? Broken { get; private set; }

    public void Load()
    {
        if (!File.Exists(Path))
        {
            Data = new JsonObject { ["schema"] = 1, ["calls"] = new JsonArray(), ["days"] = new JsonObject(), ["stops"] = new JsonObject() };
            return;
        }
        var data = Node.ReadJson(Path);
        if (data is not JsonObject o || o["calls"] is not JsonArray calls || o["days"] is not JsonObject
            || calls.Any(c => c is not JsonArray a || a.Count == 0 || KeepSet.Number(a[0]) is null))
        {
            Broken = $"{Path} does not parse - no Web API call until it is fixed or deleted";
            Data = null;
            return;
        }
        if (o["stops"] is not JsonObject)
            o["stops"] = new JsonObject();
        Data = o;
    }

    public void Save() => Files.WriteAtomic(Path, Node.Compact(Data) + "\n", FileModes.Private);

    static double Time(JsonNode? call) => call is JsonArray a && a.Count > 0 && KeepSet.Number(a[0]) is { } t ? t : double.NegativeInfinity;

    void Prune(double now)
    {
        var calls = (JsonArray)Data!["calls"]!;
        var keep = calls.Where(c => c is JsonArray a && a.Count > 0 && now - Time(c) < Day).Select(c => c!.DeepClone()).ToList();
        calls.Clear();
        foreach (var c in keep)
            calls.Add(c);
        foreach (var section in new[] { "days", "stops" })
        {
            var table = (JsonObject)Data[section]!;
            foreach (var old in table.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).SkipLast(14).ToList())
                table.Remove(old);
        }
    }

    public int Last24h(double? now = null)
    {
        var t = now ?? clock.Now();
        return Node.Arr(Data?["calls"]).Count(c => t - Time(c) < Day);
    }

    public long Count(string day) => Node.Int(Node.Get(Data?["days"], day));

    public string? Stopped(string day) => Node.Truthy(Node.Get(Data?["stops"], day)) ? Node.Str(Node.Get(Data?["stops"], day)) : null;

    public void Stop(string day, string why)
    {
        if (Data is null)
            return;
        var stops = (JsonObject)Data["stops"]!;
        if (!Node.Truthy(stops[day]))
        {
            stops[day] = why;
            Save();
        }
    }

    /// <summary>Count a call before it is made. Throws StopApiException when a cap or the day's stop says no.</summary>
    public int Reserve(string day, string api)
    {
        if (Data is null)
            throw new StopApiException(Broken ?? "the API ledger is not loaded");
        var now = clock.Now();
        Prune(now);
        if (Stopped(day) is { } stopped)
            throw new StopApiException($"stopped earlier today: {stopped}");
        if (Last24h(now) >= DayCap)
            throw new StopApiException($"the cap of {DayCap} Web API calls in 24 hours is reached");
        var calls = (JsonArray)Data["calls"]!;
        calls.Add(new JsonArray(Math.Round(now, 3), api, null));
        ((JsonObject)Data["days"]!)[day] = Count(day) + 1;
        Save();
        return calls.Count - 1;
    }

    public void Settle(int index, JsonNode? status)
    {
        try
        {
            ((JsonArray)((JsonArray)Data!["calls"]!)[index]!)[2] = status;
            Save();
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidCastException or NullReferenceException or IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>File modes the sync writes with: its own files 0644, the bridge's 0666.</summary>
public static class FileModes
{
    public const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    public const UnixFileMode Shared = Private | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
}

/// <summary>The only way this tool talks to api.steampowered.com: through the program's one WebApiDoor - one call at a time, at least
/// steam.apiGapSeconds (2 s, never less) from the end of one to the start of the next - and the ledger, with an honest User-Agent. HTTP
/// 429, 403 or 401 stops every API step for the rest of the day at once; so do 3 server errors (5xx) in a row, 5 in a run, or 5 network
/// failures in a run. A failed call is tried again after 30 s and 90 s, then given up; another 4xx is not retried.</summary>
public sealed class SteamApi : IWorkshopApi
{
    public const string QueryUrl = "https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/";
    public const string DetailsUrl = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";
    public const string VotesUrl = "https://api.steampowered.com/IPublishedFileService/GetDetails/v1/";   // with includevotes: vote_data.score
    public static readonly string[] KeyedApis = ["QueryFiles", "GetDetails"];   // GetPublishedFileDetails needs no key
    public static readonly double[] RetryWait = [30, 90];
    public const int Api5xxRow = 3, Api5xxRun = 5, ApiNetfailRun = 5;
    public static readonly string UserAgent = $"os2-sync/{typeof(SteamApi).Assembly.GetName().Version?.ToString(3) ?? "1"} (Workshop sync of a private CS2 server; server/OverStrike2.Sync)";

    readonly ApiLedger ledger;
    readonly string day;
    readonly WebApiDoor door;
    readonly Func<ApiRequest, ApiAnswer> send;
    readonly Action<double> sleep;
    readonly Action<string> log;
    public string? Key { get; }
    public int? RunCap { get; set; }
    public int Errors5xxRow { get; private set; }
    public int Errors5xx { get; private set; }
    public int Netfails { get; private set; }
    public int Calls { get; private set; }
    public Dictionary<string, int> ByApi { get; } = [];
    public string? StopReason { get; private set; }

    /// <param name="door">The program's one door (a test: one of its own on a fake clock).</param>
    /// <param name="sleep">The retry pauses (the door's clock: a test's moves).</param>
    public SteamApi(ApiLedger ledger, string day, WebApiDoor door, string? key, Func<ApiRequest, ApiAnswer> send, Action<double> sleep,
                    Action<string> log, int? runCap = null)
    {
        this.ledger = ledger;
        this.day = day;
        this.door = door;
        Key = string.IsNullOrEmpty(key) ? null : key;
        this.send = send;
        this.sleep = sleep;
        this.log = log;
        RunCap = runCap;
    }

    public string Redact(string text) => Key is null ? text : text.Replace(Key, "<key>");

    void Stop(string why)
    {
        StopReason = why;
        ledger.Stop(day, why);
        throw new StopApiException(why);
    }

    /// <summary>One logical request: up to RetryWait.Length + 1 sends, every one through the door and the ledger.</summary>
    public JsonNode? Call(string api, string url, IReadOnlyList<(string, string)>? data = null, IReadOnlyList<(string, string)>? query = null)
    {
        var attempts = 0;
        while (true)
        {
            attempts++;
            try
            {
                return SendOnce(api, url, data, query);
            }
            catch (ApiRefusedException)
            {
                throw;
            }
            catch (ApiErrorException e)
            {
                if (attempts > RetryWait.Length)
                    throw;
                log($"Web API {api} failed ({Redact(e.Message)}) - trying again in {(long)RetryWait[attempts - 1]} s");
                sleep(RetryWait[attempts - 1]);
            }
        }
    }

    JsonNode? SendOnce(string api, string url, IReadOnlyList<(string, string)>? data, IReadOnlyList<(string, string)>? parameters)
    {
        if (StopReason is { } stopped)
            throw new StopApiException(stopped);
        if (RunCap is { } cap && Calls >= cap)
            throw new StopApiException($"this run's cap of {cap} Web API calls is reached");
        var keyed = KeyedApis.Contains(api);
        if (keyed && Key is null)
            throw new StopApiException("no Web API key");        // nothing reserved, nothing sent
        var query = new List<(string, string)>(parameters ?? []);
        if (keyed)
            query.Add(("key", Key!));
        var full = url + (query.Count > 0 ? "?" + UrlEncode(query) : "");
        var body = data is null ? null : UrlEncode(data);
        var headers = new Dictionary<string, string> { ["User-Agent"] = UserAgent, ["Accept"] = "application/json" };
        if (body is not null)
            headers["Content-Type"] = "application/x-www-form-urlencoded";
        var request = new ApiRequest(body is null ? "GET" : "POST", full, body, headers);
        return door.Call(() =>
        {
            var index = ledger.Reserve(day, api);
            Calls++;
            ByApi[api] = ByApi.GetValueOrDefault(api) + 1;
            ApiAnswer answer;
            try
            {
                answer = send(request);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or SocketException or TaskCanceledException or OperationCanceledException or TimeoutException)
            {
                if (e is OperationCanceledException && StoppableClock.Instance.Stopping)
                    throw new StopRequested();
                ledger.Settle(index, "net");
                Netfails++;
                var what = Redact($"{e.GetType().Name}: {e.Message}");
                if (Netfails >= ApiNetfailRun)
                    Stop($"{Netfails} network failures talking to the Web API (the last: {what}) - every API step stops for the rest of the day");
                throw new ApiErrorException($"network: {what}");
            }
            var status = answer.Status;
            ledger.Settle(index, status);
            if (status is 401 or 403 or 429)
                Stop($"Steam's Web API answered HTTP {status} to {api} - every API step stops for the rest of the day");
            if (status is < 200 or >= 300)
            {
                if (status is >= 500 and < 600)
                {
                    Errors5xxRow++;
                    Errors5xx++;
                    if (Errors5xxRow >= Api5xxRow || Errors5xx >= Api5xxRun)
                        Stop($"Steam's Web API answered {Errors5xx} server errors ({Errors5xxRow} in a row, the last HTTP {status} to {api}) - every API step stops for the rest of the day");
                    throw new ApiErrorException($"HTTP {status}");
                }
                throw new ApiRefusedException($"HTTP {status}");
            }
            Errors5xxRow = 0;
            try
            {
                return Node.Parse(Encoding.UTF8.GetString(answer.Body), api);
            }
            catch (BadDataException)
            {
                throw new ApiErrorException($"the answer to {api} is not JSON");
            }
        });
    }

    /// <summary>Python's urllib.parse.urlencode: quote_plus of every name and value, joined with &amp;.</summary>
    public static string UrlEncode(IEnumerable<(string, string)> pairs) => string.Join("&", pairs.Select(p => QuotePlus(p.Item1) + "=" + QuotePlus(p.Item2)));

    public static string QuotePlus(string s)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' || b is (byte)'_' or (byte)'.' or (byte)'-' or (byte)'~')
                sb.Append((char)b);
            else if (b == (byte)' ')
                sb.Append('+');
            else
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    static JsonObject Response(JsonNode? reply) => Node.Truthy(Node.Get(reply, "response")) ? Node.Obj(Node.Get(reply, "response")) : [];

    public (List<JsonObject> Items, string NextCursor, long Total) QueryFiles(IReadOnlyList<string> tags, string cursor, int perPage)
    {
        var p = new List<(string, string)>
        {
            ("query_type", "1"), ("cursor", cursor), ("numperpage", perPage.ToString(CultureInfo.InvariantCulture)), ("appid", "730"), ("filetype", "0"), ("match_all_tags", "false"),
            ("return_tags", "true"), ("return_metadata", "true"), ("return_short_description", "false"), ("strip_description_bbcode", "true"),
        };
        p.AddRange(tags.Select((t, i) => ($"requiredtags[{i}]", t)));
        var response = Response(Call("QueryFiles", QueryUrl, query: p));
        var items = Node.Arr(response["publishedfiledetails"]).OfType<JsonObject>().ToList();
        return (items, Node.StrOr(response["next_cursor"]), Node.Int(response["total"]));
    }

    /// <summary>{id: [score, votes up, votes down, subscriptions]} of the items the Workshop has (result 1): IPublishedFileService/GetDetails
    /// with includevotes, 100 ids a call, the key needed (vote_data {score, votes_up, votes_down}; a removed item answers result 15 and
    /// nothing else).</summary>
    public Dictionary<string, JsonArray> Votes(IReadOnlyList<string> ids)
    {
        var p = new List<(string, string)> { ("includevotes", "true"), ("short_description", "true"), ("includetags", "false"), ("appid", "730") };
        p.AddRange(ids.Select((w, i) => ($"publishedfileids[{i}]", w)));
        var reply = Call("GetDetails", VotesUrl, query: p);
        var out_ = new Dictionary<string, JsonArray>();
        foreach (var item in Node.Arr(Response(reply)["publishedfiledetails"]))
        {
            var wid = Node.StrOr(Node.Get(item, "publishedfileid"));
            var vote = Node.Get(item, "vote_data");
            if (Text.Digits(wid) && Node.Int(Node.Get(item, "result")) == 1 && vote is JsonObject v && Node.IsNumber(v["score"]))
                out_[wid] = new JsonArray(Math.Round(Node.Float(v["score"]), 6), Node.Int(v["votes_up"]), Node.Int(v["votes_down"]), Node.Int(Node.Get(item, "subscriptions")));
        }
        return out_;
    }

    /// <summary>(ids, next cursor, total): one page of the Workshop's Most Popular for a tag - QueryFiles with query_type 3 (RankedByTrend)
    /// over `days` days, ids only, the key needed (30 days is accepted; its order differs from 7's and 90's).</summary>
    public (List<string> Ids, string NextCursor, long Total) Trend(string tag, string cursor, int days, int perPage = 100)
    {
        var p = new List<(string, string)>
        {
            ("query_type", "3"), ("days", days.ToString(CultureInfo.InvariantCulture)), ("cursor", cursor), ("numperpage", perPage.ToString(CultureInfo.InvariantCulture)), ("appid", "730"), ("filetype", "0"),
            ("requiredtags[0]", tag), ("ids_only", "true"),
        };
        var response = Response(Call("QueryFiles", QueryUrl, query: p));
        var ids = Node.Arr(response["publishedfiledetails"]).OfType<JsonObject>().Where(i => Text.Digits(Node.StrOr(i["publishedfileid"])))
            .Select(i => Node.Str(i["publishedfileid"])).ToList();
        return (ids, Node.StrOr(response["next_cursor"]), Node.Int(response["total"]));
    }

    public Dictionary<string, JsonObject> Details(IReadOnlyList<string> ids)
    {
        var form = new List<(string, string)> { ("itemcount", ids.Count.ToString(CultureInfo.InvariantCulture)) };
        form.AddRange(ids.Select((w, i) => ($"publishedfileids[{i}]", w)));
        var reply = Call("GetPublishedFileDetails", DetailsUrl, data: form);
        var out_ = new Dictionary<string, JsonObject>();
        foreach (var item in Node.Arr(Response(reply)["publishedfiledetails"]).OfType<JsonObject>())
        {
            var wid = Node.StrOr(item["publishedfileid"]);
            if (Text.Digits(wid))
                out_[wid] = item;
        }
        return out_;
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>The real request: a non-2xx answer is its status, a failure to get one an exception.</summary>
    public static ApiAnswer HttpSend(ApiRequest r)
    {
        using var req = new HttpRequestMessage(r.Method == "POST" ? HttpMethod.Post : HttpMethod.Get, r.Url);
        foreach (var (k, v) in r.Headers)
            if (k != "Content-Type")
                req.Headers.TryAddWithoutValidation(k, v);
        if (r.Body is not null)
        {
            req.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(r.Body));
            req.Content.Headers.TryAddWithoutValidation("Content-Type", r.Headers.GetValueOrDefault("Content-Type", "application/x-www-form-urlencoded"));
        }
        using var resp = Http.Send(req);
        using var s = resp.Content.ReadAsStream();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return new ApiAnswer((int)resp.StatusCode, ms.ToArray());
    }
}
