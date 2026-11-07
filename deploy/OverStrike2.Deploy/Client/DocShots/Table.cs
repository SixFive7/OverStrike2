using System.Text.RegularExpressions;

namespace OverStrike2.Deploy;

/// <summary>A box at 1920x1080. CS2 scales its HUD by the height (measured on real shots at all seven resolutions): what is centred keeps its
/// distance from the centre, times the scale; anchor l and r keep it from that edge; v is the big vote window (DocShots.Place).</summary>
sealed record Box(double X0, double Y0, double X1, double Y1, string Anchor = "c");

readonly record struct Rgb3(int R, int G, int B);

/// <summary>blur, paint (a flat fill and a test name in the HUD's look), chat (the chat zone down to y1).</summary>
sealed record Mask(string Kind, Box? Box = null, string Text = "", Rgb3? Colour = null, double Size = 13, double Spacing = 0, string Align = "l",
                   double Y1 = 975, bool Bold = true, bool Drift = false)
{
    public Rgb3 Color => Colour ?? new Rgb3(245, 245, 245);
}

/// <summary>rcon, rcon_bg, con, post, pause, shoot, mark, wait_log, vote_gap, vote_closed, guard.</summary>
sealed record Step(string Kind, string Text = "", double Seconds = 0, string Expect = "", string Button = "", int N = 1, double Gap = 0,
                   bool Parallel = false, int WaitMs = 300);

/// <summary>Whether the window is open in a row's frames: the normal window, the big vote window, or none (the chat zone stops at its edge).</summary>
enum Win { None, Window, Vote }

sealed class Row
{
    public required string Id { get; init; }
    public string Shows { get; init; } = "";
    public string Status { get; init; } = DocShots.Ready;
    public List<Step> Steps { get; init; } = [];            // how to reach it: the exact commands, the shots among them
    public string When { get; init; } = "";                 // when to shoot, in words
    public Box? Crop { get; init; }
    public Mask[] Masks { get; init; } = [];
    public string Reason { get; init; } = "";               // needs-message-system, not-triggerable: why
    public string Section { get; init; } = "";
    public string Pick { get; init; } = "";                 // the message's name in the picks file
    public string Names { get; init; } = DocShots.NoName;
    public string After { get; init; } = "";                // the row whose state this one needs
    public List<Step> Cleanup { get; init; } = [];
    public string Bridge { get; init; } = "";               // what it writes in the bridge's data
    public bool Chat { get; init; }                         // chat is its subject: its own chat mask instead of the whole zone
    public Win Win { get; init; } = Win.Window;
    public Box Avatar { get; init; } = DocShots.BlurAvatar.Box!;   // where the team counter's avatar shows in its frames
    public string Today { get; init; } = "";                // the surface today's committed code uses
    public bool StandIn { get; init; }                      // the message's real words on its real panel, sent with gg_notice
    public (string Path, string Words)? WordsFrom { get; init; }   // words that must still be in the committed code
    public bool Renamed { get; init; }                      // its own words name the client, where no mask can go: only with --renamed
    public Box[] Volatile { get; init; } = [];              // boxes that differ on every shot beyond those of its window's kind
}

/// <summary>docshots.py's table, ported row for row: every page, tab and state of the HUD, and every message of the message list. Built once the
/// deployment (the client's account, the server's address) and the committed content are known.</summary>
static partial class DocShots
{
    public const string Ready = "ready", Needs = "needs-message-system", Not = "not-triggerable";
    public static readonly string[] Statuses = [Ready, Needs, Not];
    public const string Ingame = "CSGO_GAME_UI_STATE_LOADINGSCREEN -> CSGO_GAME_UI_STATE_INGAME";
    public const string StartMap = "ar_shoots";
    public const string FakeAccount = "42424243";     // no such player on the server: css_gghud click acts as HUDtest for it (HudScreen.SimulateClick)
    public const string Me = "Alice";                 // the test name painted over the client's own name, and the who of what the run itself changes
    public const string MadeUpAccount = "42424242";   // the offline modes' client when Steam names none: no such player
    public static readonly string[] Resolutions = ["1920x1080", "1280x720", "1680x1050", "1600x1200", "2560x1080", "2560x1440", "3840x2160"];
    public static readonly string[] AllVerbs = ["rcon", "cmd", "shot", "log"];

    // what the estimate counts (seconds), as the earlier runs' logs have it
    public const double TRcon = 10.5, TShot = 2.5, TCon = 1.7, TLog = 2.0, TWaitLog = 40.0;
    // The rows' waits were tuned to an RCON call whose command ran about 2.5 s after its launch and which returned after about 10.5 s. The harness's
    // rcon verb runs its command about 1.4 s after the launch and returns in under 2 s, so the real run keeps the tuned pace: each RCON call starts
    // RconDelay late, and a waited one returns no sooner than TRcon after its step began. Retune the rows and drop these two together, never one
    // alone: a vote's rows, the HUD check's warning and the short messages' shots are timed against them.
    public const double RconDelay = 1.1;
    public const double TPost = 2.0;      // a bridgepost call: not timed yet, a guess; a vote is two execs
    public const int VoteGap = 125;       // MapVote.WaitSeconds, 120, and a little
    public const int NewsGone = 9;        // Notices.NewsSeconds, 8, and a little: the line that says a notice was taken back

    // ---------------------------------------------------------------- geometry: boxes at 1080p, placed by the height

    static Mask PaintM(double x0, double y0, double x1, double y1, string text, Rgb3? color = null, double size = 13, double spacing = 0, string align = "l",
                       string anchor = "c", bool bold = true, bool drift = false) =>
        new("paint", new Box(x0, y0, x1, y1, anchor), text, color ?? new Rgb3(245, 245, 245), size, spacing, align, Bold: bold, Drift: drift);

    static Mask BlurM(double x0, double y0, double x1, double y1, string anchor = "c") => new("blur", new Box(x0, y0, x1, y1, anchor));

    /// <summary>The chat zone but for its last so many lines: what stays is the row's subject. Chat's last line sits at y 941-962 at 1080p, 21.14 px a
    /// line (measured on a real frame of !help's answer).</summary>
    static Mask ChatAbove(int lines) => new("chat", Y1: 962 - 21.14 * lines);

    public static readonly Rgb3 Gold = new(233, 180, 76), GoldHi = new(255, 212, 126), Grey = new(170, 176, 184);
    public static readonly Mask BlurAvatar = BlurM(928, 36, 992, 102);                      // CS2's team counter shows the player's Steam avatar, on every frame
    public static readonly Box BuildInfo = new(0, 1046, 720, 1080, "l");                    // CS2's build line at the bottom left of an insecure session: not ours to show
    public static readonly Box AvatarOverNews = new(928, 36, 992, 84);                      // ...down to the top line above the open window, drawn over it
    public static readonly Mask BlurScoreboard = BlurM(505, 495, 1435, 735);                // CS2's own scoreboard: the name and the avatar
    public static readonly Mask BlurNewsWin = BlurM(480, 80, 1440, 128);                    // the top line above the open window, where a line carries the own name
    public static readonly Mask BlurSetBy = BlurM(558, 356, 720, 373);                      // "Matches last 10 minutes - set by <name>", should the state carry one
    public static readonly Mask MPlayer1 = PaintM(372, 364, 432, 384, Me, size: 13);       // SERVER, Players: the first row
    public static readonly Mask MSetBy = PaintM(388, 356, 730, 373, $"Matches last 10 minutes - Set by {Me}", Grey, 12, bold: false);   // SERVER, Game: the whole label
    // The normal window: x 340-1580, y 130-950; its body starts at 297 (130 + the rule 3, the head 76, the tabs 48, the sub-tabs 40).
    public static readonly Mask MGreet = PaintM(482, 333, 900, 355, "WELCOME, ALICE", Gold, 13, 3);      // START: x 340 + 34 + the 90 px logo + 18
    public static readonly Mask MGreetBack = PaintM(482, 333, 900, 355, "WELCOME BACK, ALICE", Gold, 13, 3);
    // The ballot's big window: x 80-1840, y 80-1000, scaled at 16:10 and 4:3 (anchor v).
    public static readonly Mask MPlayer1Vote = PaintM(112, 314, 190, 336, Me, size: 13, anchor: "v");                          // Players in the big window
    public static readonly Mask MVWait1 = PaintM(296, 304, 1040, 334, $"Waiting for: {Me}", GoldHi, 15, anchor: "v");          // the whole label, from x 299
    public static readonly Mask MVWait2 = PaintM(296, 304, 1040, 334, $"Waiting for: {Me}, HUDtest", GoldHi, 15, anchor: "v");
    public static readonly Mask BlurVWaitTail = BlurM(905, 304, 1048, 334, "v");                                              // the strip is on: a number in the words
    public static readonly Mask BlurVStart = BlurM(400, 262, 1040, 292, "v");                                                 // osvstart
    public static readonly Mask MVStart = PaintM(348, 264, 1040, 290, $"Started by {Me}", new Rgb3(245, 245, 245), 13, anchor: "v");   // osvstart, the whole label
    public static readonly Mask MVReset = PaintM(1583, 314, 1806, 340, $"Reset by {Me}", new Rgb3(255, 255, 255), 14, anchor: "v");   // the inactivity clock's words
    public static readonly Mask BlurTile1Names = BlurM(118, 492, 334, 544, "v");                                              // the first tile: who voted for it
    public static readonly Mask BlurTile1Who = BlurM(120, 568, 444, 592, "v");                                                // the first tile: picked by <own>

    public static readonly Box CTop = new(470, 140, 1450, 240), CTopWin = new(470, 76, 1450, 130), CBan = new(520, 140, 1400, 250), CPlain = new(780, 858, 1140, 930),
        CAlert = new(780, 792, 1140, 880), CCard = new(720, 600, 1200, 735), CLine = new(340, 860, 1580, 950), CChat = new(0, 740, 620, 975, "l"),
        CBoard = new(1450, 180, 1880, 900), CWhy = new(730, 250, 1190, 830), CX = new(1240, 135, 1580, 205), CVHead = new(100, 250, 1820, 352, "v"),
        CClocks = new(1040, 252, 1820, 350, "v"), CSetRow = new(360, 320, 900, 430);

    // A retake against the committed pictures (publish): a picture counts as changed only where it shows something else, not where it differs on every
    // shot. What a frame is about: the window, the big vote window, or the row's crop; what differs on every shot is left out (VolatileBoxes).
    public static readonly Box Window = new(340, 130, 1580, 950);                 // the normal window
    public static readonly Box VoteWindow = new(80, 80, 1840, 1000, "v");         // the big vote window
    public static readonly Box VMapCount = new(548, 179, 770, 199);               // the window's header: "3751 WORKSHOP MAPS", a count the sync moves
    public static readonly Box VIdle = new(1248, 914, 1558, 942);                 // the window's foot: "CLOSES WHEN IDLE" and its draining bar
    public static readonly Box VVoteMapCount = new(296, 130, 470, 147, "v");      // the big window's header count
    public static readonly Box VVoteSubtab = new(112, 216, 312, 237, "v");        // "VOTE - 51 S", the vote's seconds on its sub-tab, and BROWSE beside it
    public static readonly Box VVoteTiles = new(110, 352, 1810, 918, "v");        // the ballot's tiles: rolled at random for every vote
    public static readonly Box VOnFor = new(1470, 362, 1558, 382);                // SERVER, Players: the first row's ON FOR, a timer
    public static readonly Box VHudColumn = new(748, 362, 1110, 382);             // SERVER, Players: the first row's HUD column, where the check counts its seconds
    public static readonly Box VSyncBody = new(362, 298, 1558, 908);              // SERVER, Sync: the sync's own live status, queue and history

    /// <summary>The Mask and Box constants by their names in docshots.py, for maskview.</summary>
    public static readonly Dictionary<string, object> Named = new()
    {
        ["BLUR_AVATAR"] = BlurAvatar, ["BUILD_INFO"] = BuildInfo, ["AVATAR_OVER_NEWS"] = AvatarOverNews, ["BLUR_SCOREBOARD"] = BlurScoreboard, ["BLUR_NEWS_WIN"] = BlurNewsWin,
        ["BLUR_SETBY"] = BlurSetBy, ["M_PLAYER1"] = MPlayer1, ["M_SETBY"] = MSetBy, ["M_GREET"] = MGreet, ["M_GREET_BACK"] = MGreetBack, ["M_PLAYER1_VOTE"] = MPlayer1Vote,
        ["M_VWAIT1"] = MVWait1, ["M_VWAIT2"] = MVWait2, ["BLUR_VWAIT_TAIL"] = BlurVWaitTail, ["BLUR_VSTART"] = BlurVStart, ["M_VSTART"] = MVStart, ["M_VRESET"] = MVReset,
        ["BLUR_TILE1_NAMES"] = BlurTile1Names, ["BLUR_TILE1_WHO"] = BlurTile1Who, ["C_TOP"] = CTop, ["C_TOP_WIN"] = CTopWin, ["C_BAN"] = CBan, ["C_PLAIN"] = CPlain,
        ["C_ALERT"] = CAlert, ["C_CARD"] = CCard, ["C_LINE"] = CLine, ["C_CHAT"] = CChat, ["C_BOARD"] = CBoard, ["C_WHY"] = CWhy, ["C_X"] = CX, ["C_VHEAD"] = CVHead,
        ["C_CLOCKS"] = CClocks, ["C_SETROW"] = CSetRow, ["WINDOW"] = Window, ["VOTE_WINDOW"] = VoteWindow, ["V_MAPCOUNT"] = VMapCount, ["V_IDLE"] = VIdle,
        ["V_VOTE_MAPCOUNT"] = VVoteMapCount, ["V_VOTE_SUBTAB"] = VVoteSubtab, ["V_VOTE_TILES"] = VVoteTiles, ["V_ONFOR"] = VOnFor, ["V_HUD_COLUMN"] = VHudColumn,
        ["V_SYNC_BODY"] = VSyncBody,
    };

    // ---------------------------------------------------------------- the steps: what a row does, in order

    /// <summary>An RCON command, waited for (about 10.5 s).</summary>
    static Step Rc(string cmd, string expect = "") => new("rcon", cmd, Expect: expect);

    /// <summary>An RCON command in the background: the shots behind it see what it does.</summary>
    static Step Rb(string cmd, string expect = "") => new("rcon_bg", cmd, Expect: expect);

    static Step Show(string page, bool bg = false) => new(bg ? "rcon_bg" : "rcon", $"css_gghud show {Account} {page}", Expect: "window on ");

    /// <summary>A click through the real click path (HudScreen.Press).</summary>
    static Step Press(string button, bool bg = false) => new(bg ? "rcon_bg" : "rcon", $"css_gghud press {Account} {button}", Expect: "pressed ", Button: button);

    /// <summary>What a button sends the bridge, for that account (HudScreen.SimulateClick): the player's own name, or HUDtest for an account not on the server.</summary>
    static Step Click(string button, string? who = null, bool bg = false) =>
        new(bg ? "rcon_bg" : "rcon", $"css_gghud click {who ?? Account} {button}", Expect: "sent !", Button: button);

    /// <summary>gg_notice: the news line, for everybody: the plugin's own line wins over the bridge's while it lasts.</summary>
    static Step Note(int seconds, string text, bool bg = true) => new(bg ? "rcon_bg" : "rcon", $"gg_notice {seconds} \"{text}\"", Expect: "gg_notice: shown");

    /// <summary>The client's console (an insecure session started with -Console).</summary>
    static Step Con(string cmd, int waitMs = 300) => new("con", cmd, WaitMs: waitMs);

    static Step Pause(double seconds) => new("pause", Seconds: seconds);

    static Step Shoot(int n = 1, double gap = 0, bool parallel = false) => new("shoot", N: n, Gap: gap, Parallel: parallel);

    static Step Mark() => new("mark");

    static Step WaitIngame(double timeout = 150) => new("wait_log", Ingame, Seconds: timeout);

    static Step VoteGapStep() => new("vote_gap");

    static Step VoteClosedStep() => new("vote_closed");

    static Step Guard(double maxAge) => new("guard", Seconds: maxAge);

    /// <summary>bridgepost: what only the restart watcher or other players can send the bridge. It refuses anything but a candidate bridge on a test
    /// copy, so a row with one needs --bridge-copy.</summary>
    static Step Post(params string[] args) => new("post", string.Join(" ", args));

    public static Step Release => Rc("css_gghud release", "released:");

    static Step ResetFun => Rc($"gg_fun reset \"{Me}\"", "fun: reset");

    static Step Content(int level) => Rc($"css_gghud content {Account} {level}", @"account \d+: HUD");

    // ---------------------------------------------------------------- the table

    public const string NoName = "no player name on screen";
    public const string TestName = "test name only";

    static string Real(string how) => "real name would show: " + how;

    static readonly string Greeting = Real("START's greeting: a test name is painted over it");
    const string Unseen = " (its box from the stylesheet, never yet seen on a shot: check it on the first run)";
    static readonly string Players = Real("the Players page's first row: a test name is painted over it");
    static readonly string TopNamed = Real("the top line starts with who did it, typed by the client: only GgDocName's test name may reach this row, no mask covers it") + Unseen;
    static readonly string NewsVote = Real("the top line above the window names who started the vote: blurred (vote-started-line shows it under a test name)");

    // what a row writes in the bridge's data: such a row runs only with --bridge-copy
    const string WVote = "state.json and queues.json: the ballot's cards as shown (S.Shown, S.Met), the stock rolled anew";
    const string WLike = "votes.json: the mark, taken back afterwards";
    const string WTime = "state.json: the match length";
    const string WMap = "state.json: the map and its history";
    const string WGuard = "nothing by the code on a first click; but a card that is not risky after all would open a vote";
    const string WNotice = "nothing it keeps: a restart announced is in its memory; but no bridge but a candidate may hear it (bridgepost.py refuses)";
    const string WFakes = "state.json: the fake players, as seen (S.Seen)";

    public const string SBoard = "The board (TAB)";
    public const string SWindow = "The window: START, FUN, SERVER, HELP";
    public const string SMaps = "The window: MAPS, the map browser";
    public const string SVote = "The vote and its ballot states";
    public const string SModes = "The window in the other game modes";
    public static readonly string[] Sections = [SBoard, SWindow, SMaps, SVote, SModes];   // then "Messages: ...", one per heading of the picks file

    /// <summary>The other six resolutions, for the owner's review only (never committed: the docs take 1920x1080): every page and one of each surface.</summary>
    public static readonly string[] ReviewSet = ["page-start", "page-fun", "page-players", "page-game", "page-sync", "page-help", "page-how", "page-maps", "maps-keyboard",
        "maps-search", "board-default", "welcome-first", "join-tagline", "top-plain", "top-over-window", "hud-outdated", "restart-counting", "catchup-boost", "check-warning",
        "joined-hud-version", "chat-unknown", "why-card", "fun-refused", "vote-open", "vote-my-vote", "vote-kept", "vote-tie-lead", "window-restart-strip", "game-time-10"];

    static List<Row> rows = [];

    static void R(string id, string shows = "", string status = Ready, List<Step>? steps = null, string when = "", Box? crop = null, Mask[]? masks = null,
                  string reason = "", string section = "", string pick = "", string names = NoName, string after = "", List<Step>? cleanup = null, string bridge = "",
                  bool chat = false, Win win = Win.Window, Box? avatar = null, string today = "", bool standIn = false, (string, string)? wordsFrom = null,
                  bool renamed = false, Box[]? @volatile = null) =>
        rows.Add(new Row
        {
            Id = id, Shows = shows, Status = status, Steps = steps ?? [], When = when, Crop = crop, Masks = masks ?? [], Reason = reason, Section = section, Pick = pick,
            Names = names, After = after, Cleanup = cleanup ?? [], Bridge = bridge, Chat = chat, Win = win, Avatar = avatar ?? BlurAvatar.Box!, Today = today,
            StandIn = standIn, WordsFrom = wordsFrom, Renamed = renamed, Volatile = @volatile ?? [],
        });

    static void Top(string id, string pick, string text, (string, string) wordsFrom, string today = "the top line 4d, as picked") =>
        R(id, $"The top line: {text}", pick: pick, standIn: true, today: today, wordsFrom: wordsFrom, steps: [Note(10, text), Pause(2.5), Shoot()],
          when: "about 4 s after the send goes out in the background", crop: CTop, win: Win.None, names: Regex.IsMatch(text, @"\b(Bob|Erik|Cara)\b") ? TestName : NoName);

    static void Browser(string id, string button, string shows, List<Step>? cleanup = null, string after = "page-maps") =>
        R(id, $"MAPS > Browse: {shows}", section: SMaps, after: after, steps: [Press(button), Pause(1), Shoot()], cleanup: cleanup ?? [], when: "1 s after the click returns");

    static List<Step> ToMode(string mode, int gameType, int gameMode, string mapName) =>
        [Release, Rc($"os_mode {mode}", "os_mode: "), Rc($"game_type {gameType}"), Rc($"game_mode {gameMode}"), Mark(), Rc($"changelevel {mapName}"), WaitIngame(), Pause(6),
         Con("jointeam 2 1", 1500), Content(Level)];

    static List<Row> BuildRows()
    {
        rows = [];

        // ---- a fresh, real join first: any click ends the HUD check
        R("check-click-to-play", "The welcome window at a real join, while the HUD check waits: the X is the version button, Close this to play beside it", pick: "HUD check: Click to play",
          today: "the window's X (gg_hudver_v4.6.0, class vcheck) and its words beside it, as the picks' 7a; no chat copy",
          steps: [Con("disconnect"), Pause(3), Mark(), Con($"connect {Host}:{Port}"), WaitIngame(), Pause(4), Con("jointeam 2 1", 1500), Pause(5), Shoot()],
          when: "5 s after the team is joined: the window opens 1.5 s after the first spawn. The run's first row: any click ends the check, and only a real join brings it back",
          crop: CX, masks: [MGreetBack], names: Greeting + Unseen);
        R("players-check", "SERVER > Players while the check waits: HUD check: N s left in the HUD column", section: SWindow,
          after: "check-click-to-play", steps: [Show("players"), Pause(1), Shoot()], when: "1 s after the show returns; show is no click", masks: [MPlayer1], names: Players,
          @volatile: [VOnFor, VHudColumn]);
        R("check-warning", "The warning before the disconnect, in CS2's alert box; under HUD_CHECK warn its words say to close the window", pick: "HUD check: the disconnect warning",
          today: "CS2's alert text 10b, 15 s, and its chat lines (the words of enforce take HUD_CHECK=enforce, which no verb sets)",
          after: "players-check", steps: [Release, Pause(50), Shoot(n: 2)], when: "about 80 s after the first spawn, 5 s into the 15 s; the window closed by css_gghud release, which is no click, since it hides the box",
          crop: CAlert, win: Win.None, wordsFrom: ("bridge/HudCheck.cs", "HUD check: No click yet"));
        R("joined-hud-version", "CS2's plain centre text: who joined with which HUD", pick: "\"Erik joined with HUD v4_6_0\"", today: "CS2's plain centre text 9a, 8 s, held by sending it again, with its chat and board copies",
          standIn: true, wordsFrom: ("bridge/Program.cs", "joined with"), after: "check-warning", steps: [Rb($"css_gghud centre 3 8 \"Erik joined with HUD {ContentName}\"", "sent again"), Pause(3.5), Shoot(n: 2)], cleanup: [Content(Level)],
          when: "from about 5 s after the send. css_gghud centre sends the real text verb's words under a test name; the cleanup says which HUD this game holds without a click, whose news names the client",
          crop: CPlain, win: Win.None, names: TestName + ": the real one names the client");

        // ---- the top line, window closed: gg_notice is the real sender of the download lines; the other lines' real words on their real panel
        R("top-plain", "The top line: a plain news item, the match length set by a fake player", pick: "A plain news item (Previous, the match length, new maps, a mode's reload)", today: "the top line 4d, as picked; chat and the board too", bridge: WTime,
          steps: [Post("act", "0", "time10"), Pause(2), Shoot()], cleanup: [Post("act", "0", "time60"), Pause(NewsGone), Post("fake-off")], when: "about 4 s after the click, a fake player's, so it names a test name. The cleanup waits out the line that puts it back",
          crop: CTop, win: Win.None, names: TestName, wordsFrom: ("bridge/Program.cs", "set the match length to"));
        Top("top-server-down", "The server went down on a map", "The server crashed on #1809 bigjoe_water and has restarted - That map no longer comes up at random, and voting for it takes a second click",
            ("bridge/CrashWatch.cs", "The server crashed on"), today: "the top line 4d, 30 s; a real one records a crash mark");
        Top("top-downloading", "The server downloading a map", "Server is downloading Assembly: 61 percent (438 MB of 711 MB) - The map changes when it is ready",
            ("server/OverStrike2.Server/Workshop.cs", "the map changes when it is ready"), today: "the top line 4d by gg_notice, the real sender's own verb; a real one leaves a map on the server");
        Top("top-background-first", "Finishing a background download first", "Server is finishing the background download of Lake Aim first (63%) - Then Assembly, the map changes when it is ready",
            ("server/OverStrike2.Server/Workshop.cs", "Server is finishing the background download"), today: "the top line 4d by gg_notice, the real sender's own verb");
        Top("top-download-failed", "A download failed", "The server could not download Assembly: No connection - Pick another map", ("server/OverStrike2.Server/Workshop.cs", "The server could not download"),
            today: "the top line 4d by gg_notice, the real sender's own verb, 10 s");
        R("game-changer-undone", "The top line: a map's game-changer undone, the real thing", pick: "A map's game-changer undone", today: "the top line 4d, 10 s, as picked",
          steps: [Rb("sv_allow_votes 1"), Pause(3), Shoot(n: 2)], cleanup: [Rc("sv_allow_votes")], when: "from about 4.5 s after the send: the plugin puts the convar back within a second and says so, once per setting and map, so a setting not blocked yet on this map; the cleanup reads it back into the log",
          crop: CTop, win: Win.None, wordsFrom: ("bridge/BlockList.cs", "Undone: the server keeps its own rules"));

        // ---- the version banners: this content's own words
        R("hud-outdated", "The status banner: your HUD is out of date, the server's version faked", pick: "Your HUD is out of date: the banner that stays", today: "the status banner 4e, top centre, as picked; css_gghud level fakes the server's version",
          steps: [Rc($"css_gghud level {Level + 1} {Account}", "join banner"), Pause(1), Shoot()], cleanup: [Rc("css_gghud level 0", "server level real")], when: "1 s after the call returns: it stays", crop: CBan, win: Win.None);
        R("hud-newer", "The status banner: your HUD is newer than the server's", pick: "Your HUD is newer than the server's", today: "the status banner 4e, red as the owner's note asks, 15 s from the join (vjoin)",
          steps: [Rc($"css_gghud level {Level - 1} {Account}", "join banner"), Pause(1), Shoot()], cleanup: [Rc("css_gghud level 0", "server level real")], when: "1 s after the call returns, inside the 15 s that css_gghud level's join starts", crop: CBan, win: Win.None);

        // ---- the welcome window, in both forms
        R("join-tagline", "The welcome window at a return: the tagline's line at the foot of its page, for 3 s", pick: "Join tagline", today: "a line on the window's page, as picked, and a chat copy; the same words are the page's large text",
          steps: [Release, Rb($"css_guide {Account} back", "guide: showing"), Pause(2.4), Shoot()], when: "about 4 s after the send: the window opens 1.5 s after the command, the line ends 3 s after it",
          crop: CLine, masks: [MGreetBack], names: Greeting + Unseen);
        R("welcome-back", "The welcome window at a return: Welcome back, the map that is on, its likes, the hint", pick: "Welcome-back card", today: "the window's page, the wback panel, as picked",
          after: "join-tagline", steps: [Pause(0.5), Shoot()], cleanup: [Release], when: "the next frame, about 7 s after the send: it stands until the X", masks: [MGreetBack], names: Greeting + Unseen);
        R("welcome-first", "The welcome window at a first login: until its X", pick: "Welcome window, at every login", today: "7a, as picked: every login, until its X",
          steps: [Rc($"css_guide {Account} first", "guide: showing"), Pause(1), Shoot()], cleanup: [Release], when: "1 s after the call returns: the window opens 1.5 s after the command", masks: [MGreet], names: Greeting + Unseen);

        // ---- chat
        var chatNames = Real("the player's own typed line above the reply (its team colour): blurred");
        R("chat-unknown", "Chat: an unknown command", pick: "Command replies: unknown command, an alias taught, !help's list", today: "a private chat line, as picked",
          steps: [Con("say !foo"), Pause(1), Shoot()], when: "about 1 s after the line", crop: CChat, chat: true, win: Win.None, masks: [ChatAbove(1)], names: chatNames, wordsFrom: ("bridge/ChatCommands.cs", "Unknown command"));
        R("chat-alias-help", "Chat: an alias taught, !help's list, and the Help page it opens", pick: "Command replies: unknown command, an alias taught, !help's list",
          today: "private chat lines beside the window, which covers chat from x 340", steps: [Con("say !commands"), Pause(1.5), Shoot()], cleanup: [Release],
          when: "about 1.5 s after the line: the teach line and five lines of the list", crop: CChat, chat: true, masks: [ChatAbove(6)], names: chatNames, wordsFrom: ("bridge/ChatCommands.cs", "You typed"));
        R("chat-refused", "Chat: a refusal with its reason", pick: "The answer to your own click: refused, click again, already set", today: "a private chat line, as picked",
          steps: [Rc("gg_fun gravity moon \"Bob\"", "fun: "), Con("say !jump high"), Pause(1), Shoot()], cleanup: [ResetFun], when: "about 1 s after the line", crop: CChat, chat: true, win: Win.None,
          masks: [ChatAbove(1)], names: chatNames, wordsFrom: ("bridge/Fun.cs", "together they throw players out of most maps"));

        // ---- the window's pages, each by its tab through the real click path: a press on a closed window does nothing and still says pressed, so each opens it first
        R("page-start", "START: the logo and the large text, the steps, the live column, the gold Start a vote", section: SWindow, steps: [Show("start"), Press("gg_tab_start"), Pause(1), Shoot()], when: "1 s after the click returns", masks: [MGreet], names: Greeting + Unseen);
        R("top-over-window", "The top line above the open window: one line, 84 px from the top", section: SWindow, after: "page-start", standIn: true,
          steps: [Note(10, "Bob set the match length to 10 minutes - From the next match"), Pause(2.5), Shoot()], when: "about 4 s after the send", crop: CTopWin, masks: [MGreet], names: Greeting + Unseen, avatar: AvatarOverNews,
          wordsFrom: ("bridge/Program.cs", "set the match length to"));
        R("page-fun", "FUN: every setting at its default, the note, Random and Default", section: SWindow, steps: [Show("start"), Press("gg_tab_fun"), Pause(1), Shoot()], when: "1 s after the click returns");
        R("fun-set-by", "FUN: Gravity Moon, its row marked: Moon - set by Bob", pick: "\"set by <name>\" on settings that are not default", today: "a line on the Fun page's row, as picked; no chat or board copy yet",
          after: "page-fun", steps: [Rc("gg_fun gravity moon \"Bob\"", "fun: "), Show("fun"), Pause(1), Shoot()], when: "1 s after the show returns", names: TestName + ": gg_fun's who");
        R("fun-refused", "FUN: Super jump High clicked just as Moon gravity greys it: the bubble at the button says why", pick: "The answer to your own click: refused, click again, already set",
          today: "the bubble at the button (FunClicks), as the owner's note asks; only a click within 2 s of the grey gets one, later ones nothing: the grey has said it", after: "fun-set-by",
          steps: [Rc("gg_fun gravity normal \"Bob\"", "fun: "), Rb($"gg_fun gravity moon \"Bob\"; css_gghud press {Account} gg_f2_2", "fun: "), Pause(3.5), Shoot()], when: "about 5 s after the send, one line for both so the click comes within the 2 s: the bubble stands 8 s",
          names: TestName + ": gg_fun's who", wordsFrom: ("bridge/Fun.cs", "together they throw players out of most maps"));
        R("fun-refused-tip", "FUN: a greyed choice, and a refused click answered at its button", section: SWindow, today: "the layout's gg-ftip with the page's bubble words, .off and .refused", after: "fun-refused", steps: [Shoot()], when: "the next frame, inside the bubble's 8 s", names: TestName + ": gg_fun's who");
        R("fun-already", "Chat: a setting typed that is on already: the reply says so", pick: "The answer to your own click: refused, click again, already set", today: "a private chat line; a click on a lit choice says nothing on this content: its look says it",
          after: "fun-refused-tip", steps: [Con("say !gravity moon"), Pause(1), Shoot()], when: "about 1 s after the line", crop: CChat, chat: true, masks: [ChatAbove(1)], names: chatNames, wordsFrom: ("bridge/ChatCommands.cs", "already"));
        R("fun-idle", "FUN: friendly fire off: Instakill and Grenades x10 dimmed, Does nothing while friendly fire is off", section: SWindow, after: "fun-already",
          steps: [Rc("gg_fun ff off \"Bob\"", "fun: "), Show("fun"), Pause(1), Shoot()], when: "1 s after the show returns", names: TestName + ": gg_fun's who");
        R("fun-many", "FUN: several rows off their default, free-for-all idling friendly fire's three", section: SWindow, after: "fun-idle",
          steps: [Rc("gg_fun bhop turbo \"Bob\"; gg_fun ice on \"Bob\"; gg_fun vampire on \"Bob\"; gg_fun ffa on \"Bob\"", "fun: "), Show("fun"), Pause(1), Shoot()], cleanup: [ResetFun, Release], when: "1 s after the show returns",
          names: TestName + ": gg_fun's who");
        R("fun-one-setting", "The top line: one fun setting changed, and who", pick: "Fun: one setting changed", today: "the top line 4d, as picked, 10 s; chat and the board too", steps: [Con("say !gravity moon"), Pause(1.5), Shoot()], cleanup: [ResetFun],
          when: "about 3 s after the typed line: the change is made a frame later and said 0.5 s after that", crop: CTop, win: Win.None, names: TopNamed, renamed: true, wordsFrom: ("bridge/Fun.cs", "ChangedLine"));
        R("fun-random", "The top line: Fun's Random, who, and what it rolled", pick: "Fun: Random", today: "the top line 4d, as picked, 10 s; chat and the board too", steps: [Con("say !fun random"), Pause(1.5), Shoot()], cleanup: [ResetFun],
          when: "about 3 s after the typed line", crop: CTop, win: Win.None, names: TopNamed, renamed: true, wordsFrom: ("bridge/Fun.cs", "RandomLine"));
        R("fun-default", "The top line: Fun's Default, and who", pick: "Fun: Default", today: "the top line 4d, as picked, 10 s; chat and the board too", steps: [Rc("gg_fun gravity moon \"Bob\"", "fun: "), Con("say !fun default"), Pause(1.5), Shoot()],
          when: "about 3 s after the typed line", crop: CTop, win: Win.None, names: TopNamed, renamed: true, wordsFrom: ("bridge/Fun.cs", "DefaultLine"));
        R("fun-catchup-off", "The top line: catch-up switched, a fun setting like any other", pick: "Catch-up or grenade drops switched", today: "the top line 4d, the catalogue's FunChanged, 10 s; chat and the board too", steps: [Con("say !catchup off"), Pause(1.5), Shoot()], cleanup: [ResetFun],
          when: "about 3 s after the typed line", crop: CTop, win: Win.None, names: TopNamed, renamed: true);
        R("catchup-boost", "Catch-up's boost: the card's green rows under the crosshair", pick: "Catch-up's boost", today: "the card 3d, as picked, 5 s; that player's chat and board", steps: [Rb($"gg_catchup show {Account} 5", "catchup: showed"), Pause(2.4), Shoot()],
          when: "about 1.6 s after the command has run in the background: gg_catchup show sends what a real spawn's boost sends", crop: CCard, win: Win.None);
        R("catchup-bounty", "The leader's bounty: the card's red rows under the crosshair", pick: "The leader's bounty", today: "the card 3d, as picked, 5 s; that player's chat", steps: [Rb($"gg_catchup show {Account} lead 4", "catchup: showed"), Pause(2.4), Shoot()],
          when: "as catchup-boost", crop: CCard, win: Win.None);
        R("page-players", "SERVER > Players: the player, the team, the HUD of the check's click, how long on", section: SWindow, steps: [Show("start"), Press("gg_tab_server"), Pause(1), Shoot()],
          when: "1 s after the click returns", masks: [MPlayer1], names: Players, @volatile: [VOnFor]);
        R("players-older", "SERVER > Players: an older HUD: older - restart CS2 (css_gghud content 1)", section: SWindow, steps: [Content(1), Show("players"), Pause(1), Shoot()], cleanup: [Content(Level)],
          when: "1 s after the show returns", masks: [MPlayer1], names: Players, @volatile: [VOnFor]);
        R("page-game", "SERVER > Game: the match length, the two restarts", section: SWindow, steps: [Show("players"), Press("gg_tab_game"), Pause(1), Shoot()], when: "1 s after the click returns",
          crop: CSetRow, masks: [BlurSetBy], names: Real("a bridge state that carries a set-by: that place is blurred"));
        R("game-restart-armed", "SERVER > Game: the restart after its first click, Press again; the line says click again", pick: "The answer to your own click: refused, click again, already set",
          today: "a line on the window's page, as picked, and the button's own words; NEVER a second click: it quits the server", steps: [Pause(31), Show("game"), Press("gg_set_restart", bg: true), Pause(3.5), Shoot()],
          when: "about 5 s after the one click. The 31 s first: a click within 30 s of an earlier one would be the second", crop: CLine, wordsFrom: ("bridge/Restarts.cs", "A restart kicks everyone"));
        R("game-time-10", "SERVER > Game: Matches last 10 minutes - Set by, the lit button", section: SWindow, bridge: WTime,
          steps: [Show("game"), Post("act", "0", "time10"), Pause(2), Shoot()], cleanup: [Post("act", "0", "time60"), Pause(NewsGone), Post("fake-off"), Release], when: "about 4 s after a fake player's click, so the page and the top line name a test name; the cleanup waits out the line that puts it back", crop: CSetRow, names: TestName,
          avatar: AvatarOverNews, wordsFrom: ("bridge/HudBrowser.cs", "Matches last"));
        R("page-sync", "SERVER > Sync: what the map sync does, its queue, its history", section: SWindow, steps: [Show("sync"), Pause(1), Shoot()], when: "1 s after the show returns. Live numbers and Workshop map names; no player name in the block (bridge/SyncDash.cs)",
          @volatile: [VSyncBody]);
        foreach (var (id, b, w) in new[] { ("sync-queue-next", "gg_osdq_next", "the queue's next page"), ("sync-history-next", "gg_osdr_next", "the history's next page"), ("sync-maps-next", "gg_osdm_next", "the map events' next page"),
                                           ("sync-maps-last", "gg_osdm_last", "the map events' last page") })
            R(id, $"SERVER > Sync: {w}", section: SWindow, after: "page-sync", steps: [Press(b), Pause(1), Shoot()], when: "1 s after the click returns", @volatile: [VSyncBody]);
        R("page-help", "HELP > Help: where everything is", section: SWindow, after: "page-sync", steps: [Press("gg_tab_help"), Pause(1), Shoot()], when: "1 s after the click returns");
        R("page-how", "HELP > How it works: every rule and chance of a map pick", section: SWindow, after: "page-help", steps: [Press("gg_tab_how"), Pause(1), Shoot()], cleanup: [Press("gg_sub_help")], when: "1 s after the click returns");

        // ---- the map browser
        R("page-maps", "MAPS > Browse: eight cards by rank, sort, mode, size, search, Previous, pager", section: SMaps, steps: [Show("maps"), Press("gg_tab_maps"), Pause(1), Shoot()], when: "1 s after the click returns");
        Browser("maps-sort-votes", "gg_sort_votes", "sorted by likes");
        Browser("maps-sort-name", "gg_sort_name", "sorted by name");
        Browser("maps-sort-size", "gg_sort_size", "sorted by size");
        Browser("maps-sort-size-desc", "gg_sort_size", "sorted by size, the other way (a second click)", cleanup: [Press("gg_sort_rank")], after: "maps-sort-size");
        Browser("maps-mode-armsrace", "gg_fm_ar", "the mode filter on Arms Race");
        Browser("maps-mode-deathmatch", "gg_fm_dm", "the mode filter on Deathmatch");
        Browser("maps-mode-wingman", "gg_fm_wm", "the mode filter on Wingman", cleanup: [Press("gg_fm_all")]);
        Browser("maps-size-tiny", "gg_fl_tiny", "the size chip under 25 MB");
        Browser("maps-size-small", "gg_fl_small", "the size chip 25-120");
        Browser("maps-size-mid", "gg_fl_mid", "the size chip 120-400");
        Browser("maps-size-large", "gg_fl_large", "the size chip 400 MB and up", cleanup: [Press("gg_fl_all")]);
        Browser("maps-liked", "gg_fl_liked", "Liked by me", cleanup: [Press("gg_fl_liked")]);
        Browser("maps-official", "gg_fl_official", "Official", cleanup: [Press("gg_fl_official")]);
        Browser("maps-ours", "gg_fl_ours", "Our maps", cleanup: [Press("gg_fl_ours")]);
        Browser("maps-page-2", "gg_pg_next", "the second page");
        Browser("maps-page-last", "gg_pg_last", "the last page", cleanup: [Press("gg_pg_first")]);
        Browser("maps-keyboard", "gg_q_kbd", "the on-screen keyboard, four cards left showing", cleanup: [Press("gg_kb_done")]);
        R("maps-search", "MAPS > Browse: a search: its best matches first", section: SMaps, after: "page-maps", steps: [Con("say !find lake"), Pause(2), Shoot()], when: "2 s after the line");
        Browser("maps-search-keyboard", "gg_q_kbd", "the keyboard over a search that is on", cleanup: [Press("gg_kb_done"), Press("gg_q_clear")], after: "maps-search");
        R("maps-search-empty", "MAPS > Browse: a search with no match, the empty page's words", section: SMaps, after: "page-maps", steps: [Con("say !find zzqqzz"), Pause(2), Shoot()], cleanup: [Press("gg_q_clear")], when: "2 s after the line");
        R("maps-workshop-refused", "MAPS > Browse: a pasted Workshop id the server refuses: the card says why (a non-map of the committed broken.json)", section: SMaps, after: "page-maps",
          steps: [Con("say 3369214545"), Pause(3), Shoot()], cleanup: [Press("gg_q_clear")], when: "3 s after the line: the server's own no needs no Workshop question", wordsFrom: ("bridge/broken.json", "3369214545"));
        R("maps-workshop-ready", "MAPS > Browse: a pasted Workshop id not on the list: Asking the Workshop, then Download and play. NEVER clicked: it downloads to the server", section: SMaps, after: "page-maps",
          steps: [Con("say 3823993566"), Pause(0.3), Shoot(n: 3)], cleanup: [Press("gg_q_clear")], when: "from about 2 s after the line, serially: the Web API answers within a few seconds (the HUD item's public id)");
        R("maps-liked-card", "MAPS > Browse: a card I like: its arrow lit, the line", section: SMaps, after: "page-maps", bridge: WLike, steps: [Press("gg_m2_up", bg: true), Pause(3.5), Shoot()], cleanup: [Press("gg_m2_down")],
          when: "about 5 s after the click: the line lasts 8 s");
        R("why-card", "Why the dislike?: the small dialog at the centre", pick: "Why the dislike? (the reasons)", today: "one small dialog at the centre, as the owner's note asks, 15 s; it shows only while the player has a cursor", after: "page-maps",
          bridge: WLike, steps: [Press("gg_m3_down", bg: true), Pause(3.5), Shoot()], cleanup: [Press("gg_m3_up")], when: "about 5 s after the click: it stands 15 s", crop: CWhy);
        R("maps-risky-card", "MAPS > Browse: a risky map's card: the red strip with its record, Vote anyway?", section: SMaps, after: "page-maps", bridge: WGuard, steps: [Con("say !find jb_moti2"), Pause(2), Shoot()],
          when: "2 s after the line (a map on the committed broken.json, 3 of 3 test loads)", wordsFrom: ("bridge/broken.json", "\"jb_moti2\""));
        R("play-asks-first", "Play on a risky map, the first click: Click again, the padlock, and the line says why. NEVER a second click: it would put the map to the vote", pick: "Play asks first (a broken or crashed map)",
          today: "the card's button and a line on the window's page, 30 s, as picked", after: "maps-risky-card", bridge: WGuard, steps: [Press("gg_m1_play", bg: true), Pause(3.5), Shoot()], cleanup: [Press("gg_q_clear"), Release],
          when: "about 5 s after the one click", crop: CLine, wordsFrom: ("bridge/Program.cs", "to vote anyway"));

        // ---- the board: +showscores from the client's console, which holds TAB down for the hidden client. Three lines first: Lately keeps real names otherwise
        var board = Real("CS2's own scoreboard beside it, the name and the avatar: its rows are blurred; Lately is filled with test names first, because real lines stay on it");
        List<Step> lately = [Note(10, "Bob set the match length to 10 minutes - From the next match", bg: false), Note(10, "Cara set the match length to 60 minutes - From the next match", bg: false),
                             Note(10, "Erik set the match length to 10 minutes - From the next match", bg: false)];
        R("board-default", "The board beside the scoreboard: your like, Start a vote, the vote line, the full menu, Lately", section: SBoard, steps: [Release, .. lately, Con("+showscores", 1500), Pause(2), Shoot()], cleanup: [Con("-showscores", 1500)],
          when: "2 s after +showscores", crop: CBoard, win: Win.None, masks: [BlurScoreboard], names: board);
        R("board-liked", "The board with your like on the map that is on", section: SBoard, bridge: WLike, steps: [Press("gg_b_vup"), .. lately, Con("+showscores", 1500), Pause(2), Shoot()], cleanup: [Con("-showscores", 1500), Press("gg_b_vdown")],
          when: "2 s after +showscores", crop: CBoard, win: Win.None, masks: [BlurScoreboard], names: board);
        R("board-why", "Why the dislike? under the TAB cursor", pick: "Why the dislike? (the reasons)", today: "the same small dialog, under the board's cursor", bridge: WLike, steps: [Press("gg_b_vdown", bg: true), Con("+showscores", 1000), Pause(1), Shoot()],
          cleanup: [Con("-showscores", 1500), Press("gg_b_vup")], when: "about 4.5 s after the click: it stands 15 s", crop: CWhy, win: Win.None, masks: [BlurScoreboard], names: board);
        foreach (var (id, b, w) in new[] { ("board-big", "gg_why_2", "You marked this map too big (the dislike's reason)"), ("board-ads", "gg_why_3", "You marked this map: ads (the dislike's reason)") })
            R(id, $"The board with my mark: {w}", section: SBoard, bridge: WLike, steps: [Press(b), .. lately, Con("+showscores", 1500), Pause(2), Shoot()], cleanup: [Con("-showscores", 1500), Press("gg_b_vup")],
              when: "2 s after +showscores; the opposite vote takes the mark back", crop: CBoard, win: Win.None, masks: [BlurScoreboard], names: board);

        // ---- the vote. Solo first (nobody but the client present, so everybody has voted once it does), the fake's last: HUDtest stays present in the bridge's memory
        Mask[] v1Masks = [BlurNewsWin, MVStart, MVWait1];
        Mask[] v1Voted = [BlurNewsWin, MVStart, MVReset, BlurTile1Names];   // the strip on, the label says only Everybody has voted: no name in it
        var v1Names = Real("the waiting line: a test name is painted over it; the top line, which names who started the vote: blurred (vote-started-line shows it under a test name)") + Unseen;
        var v1NamesVoted = Real("reset by: a test name on the clock, the header's blurred; the top line and the voter's name on the first tile: blurred") + Unseen;
        R("vote-open", "The ballot: everybody's window big, open and frozen, ten tiles, Random, the clocks, Waiting for", pick: "The vote: everybody's window opens and freezes", today: "the big vote window, as picked; its feeds still move",
          bridge: WVote, steps: [VoteGapStep(), Rc("mp_restartgame 1"), Pause(3), Click("gg_b_next"), Show("vote", bg: true), Pause(0.5), Shoot()], when: "about 10 s into the vote; the show's reply says whether a vote opened. A fresh match first: the run's second half must not meet the 60-minute match end",
          masks: v1Masks, names: v1Names, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-limit", "The ballot's limit clock: its seconds left, how many have voted", pick: "The 60 s limit", today: "a line on the window's page, as picked", after: "vote-open", bridge: WVote, steps: [Shoot()], when: "the next frame",
          crop: CClocks, masks: v1Masks, names: v1Names, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-my-vote", "The ballot: my vote lit, LEADING, the strip, Everyone has voted", section: SVote, after: "vote-limit", bridge: WVote, steps: [Press("gg_m1_play", bg: true), Pause(2.5), Shoot()],
          when: "about 4 s after the click, Keep (tile 1): the inactivity timer runs 5 s from the vote", crop: CVHead, masks: v1Voted, names: v1NamesVoted, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-inactivity", "The inactivity timer, and who reset it", pick: "Inactivity timer, and who reset it", today: "a line on the window's page, as picked", after: "vote-my-vote", bridge: WVote, steps: [Shoot()], when: "the next frame, about 6.5 s after the click",
          crop: CVHead, masks: v1Voted, names: v1NamesVoted, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-kept", "The ballot's header: the vote kept the map, Closes in, everybody still frozen", pick: "The vote kept the map", today: "a line on the window's page, as picked, 8 s; chat and the board too", after: "vote-inactivity", bridge: WVote, steps: [VoteClosedStep(), Pause(1), Shoot(n: 2)],
          when: "the next frames: decided about 7.5 s after the click, the window stays 8 s", crop: CVHead, masks: [BlurNewsWin, MVStart, BlurTile1Names], names: Real("who started it, the voter's name on the first tile: blurred") + Unseen, win: Win.Vote, avatar: AvatarOverNews, wordsFrom: ("bridge/HudVote.cs", "The vote kept"));
        R("vote-cannot-call", "The answer to a Start a vote in the wait after a vote: when the next one can start", pick: "You cannot call a vote yet", today: "a line on the window's page, as picked, 8 s; chat and the board name who tried",
          after: "vote-kept", bridge: WVote, steps: [Pause(8), Show("start"), Guard(100), Click("gg_b_next", bg: true), Pause(3.5), Shoot()], cleanup: [Release], when: "about 5 s after the click, inside the 2 minutes (the guard refuses the click otherwise: it would open a vote)",
          crop: CLine, masks: [MGreet], names: Greeting + Unseen, wordsFrom: ("bridge/VoteRound.cs", "You can call a vote again in"));
        R("board-wait", "The board in the wait after a vote: Start a vote off, its words say how long", section: SBoard, after: "vote-cannot-call", bridge: WVote,
          steps: [.. lately, Guard(100), Con("+showscores", 1500), Pause(2), Shoot()], cleanup: [Con("-showscores", 1500)], when: "2 s after +showscores, inside the 2 minutes; the three lines first, because the vote's own lines on Lately name the client",
          crop: CBoard, win: Win.None, masks: [BlurScoreboard], names: board);
        R("vote-browse", "The vote's Browse sub-tab: the map browser that adds to the vote: Add to the vote, On the vote", section: SVote, bridge: WVote, steps: [Mark(), VoteGapStep(), Click("gg_b_next"), Press("gg_v_browse", bg: true), Pause(3.5), Shoot()],
          when: "about 5 s after the click", masks: [BlurNewsWin], names: NewsVote + Unseen, avatar: AvatarOverNews);
        R("vote-picked-card", "The ballot with a map a player put on it: PICKED, picked by, their vote on it", section: SVote, after: "vote-browse", bridge: WVote, steps: [Press("gg_v_back", bg: true), Pause(3), Con("say !play ar_baggage"), Pause(0.3), Shoot()],
          when: "about 2 s after the line, inside the 5 s of the inactivity timer. An official map, so the client loads it without a dialog", masks: [BlurNewsWin, MVStart, BlurTile1Names, BlurTile1Who, MVReset],
          names: Real("the first tile's voter and picked by, the header's reset by and the top line: blurred; the clock's reset by: a test name is painted over it") + Unseen, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-decided", "The vote decided: NEXT MAP, The vote chose ... changing in N s, the top line, everybody still frozen", pick: "The vote decided, and the change counting down", today: "the ballot's words and the top line 4d; the countdown banner stays hidden under the window",
          after: "vote-picked-card", bridge: WVote + "; " + WMap, steps: [VoteClosedStep(), Pause(2.5), Shoot(n: 3)], when: "from about 7 s after the line: decided at 5 s, the change goes out at 13 s", crop: CVHead, masks: [MVStart, BlurTile1Names, BlurTile1Who],
          names: Real("the first tile's voter and picked by: blurred") + Unseen, win: Win.Vote, avatar: AvatarOverNews, wordsFrom: ("bridge/VoteRound.cs", "Everybody has voted:"));
        R("now-playing", "The card under the crosshair: Now playing, the map's own picture, at the new map's first spawn", pick: "Now-playing card, at each map's first spawn", today: "the card 3c, as picked, 8 s; picked by the vote: a Change's by is only ever the vote or the dice",
          after: "vote-decided", bridge: WVote + "; " + WMap, steps: [WaitIngame(), Pause(3), Con("jointeam 2 1", 1000), Rb("css_gghud release", "released:"), Pause(3), Shoot(n: 3)], cleanup: [Content(Level)], when: "six frames from the moment the client is in game: the card comes 1.5 s after the spawn, for 8 s", crop: CCard, win: Win.None);
        R("vote-started-line", "The ballot's header: Started by HUDtest, the real line under a test name", pick: "The vote started: the line for everybody", today: "NOT SHOWN on a surface of its own: the header of the window that opens, as the owner's note asks; chat and the board keep the line",
          bridge: WVote, steps: [VoteGapStep(), Click("gg_b_next", who: FakeAccount, bg: true), Pause(3.5), Shoot()], when: "about 5 s after the click goes out", crop: CVHead, masks: [MVWait2],
          names: Real("the waiting line after HUDtest: a test name is painted over it") + Unseen, wordsFrom: ("bridge/VoteRound.cs", "started a vote for the next map"), win: Win.Vote, avatar: AvatarOverNews);
        R("vote-random-lit", "The ballot: my vote on Random, its button lit", section: SVote, after: "vote-started-line", bridge: WVote, steps: [Press("gg_v_random", bg: true), Pause(3.5), Shoot()], cleanup: [Press("gg_m1_play", bg: true)],
          when: "about 5 s after the click. The cleanup moves the vote to Keep at once, also after a failure: Random winning would roll a Workshop map, and the client would hang in CS2's download dialog", names: TestName + ": Waiting for: HUDtest", win: Win.Vote, avatar: AvatarOverNews);
        R("vote-waiting", "The ballot: Waiting for: HUDtest, my vote on Keep, 1 of 2 voted", pick: "Waiting for: the players who have not voted", today: "a line on the window's page, as picked; no chat or board copy yet", after: "vote-random-lit", bridge: WVote, steps: [Pause(3.5), Shoot()],
          when: "about 5 s after the cleanup's click", crop: CVHead, masks: [BlurTile1Names], names: Real("the voter's name on the first tile: blurred; Waiting for: HUDtest stays") + Unseen, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-pager", "The ballot's pager: Page 1 of 2, the hint for the other page (css_gghud content fakes an older game's pages of eight)", section: SVote, after: "vote-waiting", bridge: WVote, steps: [Rb($"css_gghud content {Account} {Level - 1}", @"account \d+: HUD"), Pause(3.5), Shoot()],
          when: "about 5 s after the send", masks: [BlurTile1Names], names: Real("the voter's name on the first tile: blurred") + Unseen, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-players", "SERVER > Players during a vote: who has voted", section: SWindow, after: "vote-pager", bridge: WVote, steps: [Show("players", bg: true), Pause(3.5), Shoot()], when: "about 5 s after the send, about 45 s into the vote",
          masks: [MPlayer1Vote], names: Players + Unseen, win: Win.Vote, avatar: AvatarOverNews);
        R("vote-limit-low", "The limit clock red and pulsing in its last 10 seconds", section: SVote, today: "the stylesheet's .gg-screen.vlow, HudVote.LowSeconds", after: "vote-players", bridge: WVote, steps: [Show("vote", bg: true), Pause(6), Shoot()], cleanup: [Content(Level), Pause(3), VoteClosedStep()],
          when: "about 55 s into the vote; the cleanup waits for its 60 s to end", crop: CClocks, masks: [BlurTile1Names], names: Real("the voter's name on the first tile: blurred") + Unseen, win: Win.Vote, avatar: AvatarOverNews);

        // ---- what only the bridge's own callers can say: bridgepost, to a candidate bridge on a test copy and nothing else. The real senders' notices, fake players' clicks
        R("restart-after-match", "The status banner: the server restarts after this match", pick: "The server restarts after this match", today: "the status banner 4e, from the bridge's RestartDue, as picked; chat and the board too", bridge: WNotice,
          steps: [Release, Post("restart", "our-update", "match-end", "0"), Pause(2), Shoot()], cleanup: [Post("restart-cancel", "our-update"), Pause(NewsGone)], when: "about 4 s after the notice, the watcher's own; the cleanup takes it back and waits out the line that says so",
          crop: CBan, win: Win.None, wordsFrom: ("bridge/Restarts.cs", "The server restarts after this match"));
        R("restart-counting", "The countdown banner with the logo: the restart", pick: "The restart counting down (10 s)", today: "the countdown 6b with the logo, from the bridge's RestartDue, as the owner's note asks; chat and the board too", bridge: WNotice,
          steps: [Post("restart", "daily", "now", "12"), Pause(2), Shoot(n: 2)], cleanup: [Post("restart-cancel", "daily"), Pause(NewsGone)],
          when: "about 4 s and 6.5 s into 12 s: the real one counts 10 (Watch.RestartCountdown), a little more here so that a slow call still shows digits; only the restart watcher would quit, and a test copy has none", crop: CBan, win: Win.None, wordsFrom: ("bridge/Restarts.cs", "the server restarts in"));
        R("window-restart-strip", "The window's foot: a restart coming, its words and its countdown", section: SWindow, today: "gg-rstrip, from HudState.RestartKind", bridge: WNotice,
          steps: [Post("restart", "daily", "now", "30"), Show("start", bg: true), Pause(3.5), Shoot()], cleanup: [Post("restart-cancel", "daily"), Release], when: "about 5 s after the show goes out, of 30 s; the release outlasts the line that takes it back", crop: CLine, masks: [MGreet], names: Greeting + Unseen);
        R("top-cs2-update", "The top line: a CS2 update: the server restarts at this match's end, and restart CS2 afterwards", pick: "A CS2 update: update your game", today: "the top line 4d, as picked, until the restart: the watcher's own notice; chat and the board too", bridge: WNotice,
          steps: [Post("restart", "game-update", "match-end", "0"), Pause(2), Shoot()], cleanup: [Post("restart-cancel", "game-update"), Pause(NewsGone)], when: "about 4 s after the notice; the cleanup takes it back and waits out the line that says so", crop: CTop, win: Win.None, wordsFrom: ("bridge/Restarts.cs", "A CS2 update"));
        R("top-updated", "The top line: Updated to CS2 1.41.9.6 - Welcome back", pick: "\"Updated to vX\" after the reconnect", today: "the top line 4d, as picked, 60 s: the watcher's own notice; chat and the board too", bridge: WNotice,
          steps: [Post("updated", "game-update", "CS2", "1.41.9.6"), Pause(2), Shoot()], when: "about 4 s after the notice. It stands 60 s: updated-joiner's reconnect outlasts most of that", crop: CTop, win: Win.None, wordsFrom: ("bridge/Restarts.cs", "Welcome back"));
        R("updated-joiner", "The line for a player who joins within 10 minutes of an update (the catalogue's UpdatedJoiner; no row of its own in the picks)", section: SWindow, today: "a line on the window's page, 15 s; that player's chat and board", after: "top-updated", bridge: WNotice,
          steps: [Con("disconnect"), Pause(3), Mark(), Con($"connect {Host}:{Port}"), WaitIngame(), Con("jointeam 2 1", 1000), Pause(2.5), Shoot(n: 3)], cleanup: [Content(Level), Release],
          when: "three frames from about 7 s after the client is in game: the line stands 15 s from entered the game, the window opens 1.5 s after the first spawn. A real join asks the HUD check again: the cleanup says which HUD this game holds",
          crop: CLine, masks: [MGreetBack], names: Greeting + Unseen, wordsFrom: ("bridge/Restarts.cs", "Messages.UpdatedJoiner"));
        R("vote-tie-lead", "The ballot with several voters: TIE on three cards, then LEADING with its voters' names and the strip in parts", section: SVote, today: "the tiles' vtie, vlead, the strip's segments", bridge: WVote + "; " + WFakes,
          steps: [VoteGapStep(), Post("fake-off"), Post("fake-on", "4"), Post("client-off", Account), Post("act", "0", "next"), Post("vote", "0", "1"), Post("vote", "1", "2"), Post("vote", "2", "3"), Pause(1), Shoot(), Post("vote", "2", "1"), Pause(1), Shoot()],
          cleanup: [Post("vote", "0", "1"), Post("vote", "1", "1"), Post("vote", "2", "1"), Post("vote", "3", "1")],
          when: "a second after the third vote, and a second after one more moves to Keep (card 1). The fourth fake does not vote, so the inactivity timer does not run; the bridge takes the client for gone, so only test names are in its lines. The cleanup moves every vote to Keep: a tie is drawn by the dice, and a rolled Workshop map would hang the client in CS2's download dialog",
          crop: CVHead, names: TestName + ": the client is out of the bridge's lists", win: Win.Vote, avatar: AvatarOverNews);
        R("chat-vote-clicked", "A vote clicked on a card, in chat: who voted, and who still has to", pick: "Lines for everybody: a vote clicked on a card", today: "NOT SHOWN on a surface of its own: chat and the board", after: "vote-tie-lead", bridge: WVote + "; " + WNotice,
          steps: [Post("restart", "daily", "now", "30"), Pause(1), Shoot()], cleanup: [Post("restart-cancel", "daily"), Pause(NewsGone), VoteClosedStep(), Post("client-on", Account), Post("fake-off")],
          when: "a second after a restart's notice has ended the vote: the big window covers chat, which comes back with the lines still there. The cleanup takes the notice back and has the client present again", crop: CChat, chat: true, win: Win.None, masks: [ChatAbove(6)], names: TestName + ": the client is out of the bridge's lists", wordsFrom: ("bridge/VoteRound.cs", "voted to keep"));
        R("rematch-countdown", "Restart match: the countdown banner, naming who", section: SWindow, today: "the countdown 6b, the catalogue's MatchRestart; chat and the board too", bridge: WNotice,
          steps: [Post("act", "0", "rematch"), Post("act", "0", "rematch"), Pause(1), Shoot()], cleanup: [Pause(10), Post("fake-off")], when: "about 3.5 s after a fake player's second click, of 10 s; the cleanup waits for the match to restart", crop: CBan, win: Win.None, wordsFrom: ("bridge/Restarts.cs", "is restarting the match in"));

        // ---- the other game modes: the header's mode, friendly fire's rows and the match length gone. The last rows: back on Arms Race, ready for the next resolution
        R("mode-deathmatch-fun", "FUN in Deathmatch: friendly fire's rows, the grenade drops and catch-up gone", section: SModes, bridge: WMap, steps: [.. ToMode("deathmatch", 1, 2, "de_dust2"), Show("fun"), Pause(1), Shoot()], when: "1 s after the show returns");
        R("mode-deathmatch-game", "SERVER > Game in Deathmatch: no match length", section: SModes, after: "mode-deathmatch-fun", bridge: WMap, steps: [Press("gg_tab_game"), Pause(1), Shoot()], when: "1 s after the click returns", masks: [BlurSetBy], names: Real("a set-by in the bridge's state: blurred"));
        R("mode-wingman-fun", "FUN in Wingman", section: SModes, after: "mode-deathmatch-game", bridge: WMap, steps: [.. ToMode("wingman", 0, 2, "de_inferno"), Show("fun"), Pause(1), Shoot()], when: "1 s after the show returns");
        R("mode-wingman-game", "SERVER > Game in Wingman", section: SModes, after: "mode-wingman-fun", bridge: WMap, steps: [Press("gg_tab_game"), Pause(1), Shoot()], when: "1 s after the click returns", masks: [BlurSetBy], names: Real("a set-by in the bridge's state: blurred"));
        R("mode-armsrace-back", "START, back on Arms Race: the run's last frame", section: SModes, after: "mode-wingman-game", bridge: WMap, steps: [.. ToMode("armsrace", 1, 0, StartMap), Show("start"), Pause(1), Shoot()], cleanup: [Release], when: "1 s after the show returns",
          masks: [MGreet], names: Greeting + Unseen);

        // ---- at the very end, each leaves the bridge believing the server goes down, so each sets it right after itself: what must be called off in time
        R("top-player-restart", "The countdown banner: a player's restart, naming who", pick: "A player's !restart", today: "the countdown 6b with the logo, as the owner's note asks, 10 s; a real second click quits the server, so the bridge is told the server is going down before it would", bridge: WNotice,
          steps: [Post("act", "0", "restart"), Post("act", "0", "restart"), Pause(1), Shoot()], cleanup: [Post("quit-notice"), Post("fake-off"), .. ToMode("armsrace", 1, 0, StartMap)],
          when: "about 3.5 s after a fake player's second click, of 10 s: the cleanup's quit notice must reach the bridge before the 10 s are up", crop: CBan, win: Win.None, wordsFrom: ("bridge/Restarts.cs", "is restarting the server in"));
        R("map-change-banner", "The countdown banner with the map's picture", pick: "Map change counting down", today: "the countdown 6b, as picked; a vote's change keeps everybody's window over it, so this is the dice's own: three players timing out on a map mark it and roll, called off in time. Marks the map in the candidate's data: once per copy and map (ar_pool_day, then ar_shoots_night on 2026-11-05)", bridge: WMap + "; state.json: the map's player-crash mark; " + WFakes,
          steps: [.. ToMode("armsrace", 1, 0, "ar_shoots_night"), Post("fake-off"), Post("fake-on", "3"), Rb("css_gghud release", "released:"), Pause(3), Post("fake-crash"), Pause(1.5), Shoot()], cleanup: [Post("quit-notice"), Post("fake-off"), .. ToMode("armsrace", 1, 0, StartMap)],
          when: "about 3.5 s after the third time-out, of 8 s: the cleanup's quit notice must reach the bridge before the 8 s are up: the change would hang the client in CS2's download dialog", crop: CBan, win: Win.None, names: TestName);

        // ---- what cannot be shot yet
        R("hud-hold", "The status banner: Steam is not showing the HUD, and back", pick: "The HUD not shown by Steam, and back", status: Not, today: "the status banner 4e, set by the plugin's own PostBanner only",
          reason: "Its trigger is Steam hiding the item, and no verb sets the banner: gg_notice goes to the top line now. A test verb like css_gghud centre would make it a row.");
        R("hud-join-warning", "The join's version banner", pick: "Your HUD is out of date: the warning at the join", status: Not, today: "none: the catalogue has no kind for it, a row that went",
          reason: "The committed content has no panel for it (only the contents before v4_6_0 read svge/svlt); a game holding one is a published item, not committed code. The picks drop it: NOT SHOWN.");
        R("vote-line-closed-window", "The vote's line for a closed window", pick: "The vote line for a closed window", status: Not, today: "none: the panel's feed is gone (9685f17), a row that went",
          reason: "Removed from the plugin: the window is held open while the vote freezes. The picks drop it: NOT SHOWN; the owner's note says it should not be possible.");
        R("picture-on-request", "A map's picture, on request", pick: "A map's picture, on request", status: Not, today: "none: the picture buttons and HudPreview's plugin half are gone (9685f17), a row that went",
          reason: "Removed, as the owner's note asks: the picture belongs in the window and the browser, and the cards' pictures are not clickable. The picks: NOT SHOWN.");
        R("workshop-card-downloading", "A Workshop card's progress", pick: "A Workshop card downloading to the server", status: Not, today: "the gg_ws card's bar, with chat and board copies",
          reason: "Its progress needs gg_ws_play, which downloads the map into the server's Workshop cache: a map left on production's volume. No verb fakes the step. maps-workshop-ready shows the card up to the click.");
        R("crash-note", "The joiner's crash note", pick: "The joiner's crash note", status: Not, today: "the welcome page's crash line, for 10 minutes after a crash",
          reason: "Needs a real crash of the server the client is on, which records a crash mark on the map (CrashWatch.RecordCrash); no verb sends its words. top-server-down shows the crash's other line.");
        R("no-hud-alert", "This server uses the menu", pick: "\"This server uses the menu: press TAB\" (only without our HUD)", status: Not, today: "none: the words are gone, a row that went",
          reason: "Its condition no longer exists: hud.addon is required and a server without the HUD cannot start (commits 78c6698 and 9685f17), and the catalogue has no kind for it. The owner's note on it asked the same: is this an error state?");
        R("vote-match-end", "The vote at a match's end: Play this map again, nobody voting is Random", section: SVote, status: Not, today: "the same ballot, MatchEnded",
          reason: "Under the maintenance flag a match end opens nothing (Program.OnMatchEnd), and no cs2client verb takes the flag off; without the flag the dice roll a Workshop map at a start, and the client hangs in CS2's download dialog (HARNESS.md).");
        R("vote-second-page", "A real second page of the ballot", section: SVote, status: Not, today: "more than ten tiles",
          reason: "One player's picks replace the rolled cards and leave when they lose their vote, so a solo run never holds more than ten; several players' picks would. vote-pager shows the pager's look under pages of eight.");
        R("hold-no-menu", "The chat line for a player without the HUD during a hold (the catalogue's HoldNoMenu; no row of its own in the picks)", section: SWindow, status: Not, today: "a chat line, from the plugin's own hold",
          reason: "As hud-hold: only a real hold by Steam sets it, and the client, which holds the HUD by its loose files, is not who it is for.");
        R("hover-only", "What only a mouse hover shows: who voted on a tile (gg-mpop), a greyed Fun choice's tip (gg-ftip), the buttons' hover looks", section: SVote, status: Not, today: "the stylesheet's :hover rules",
          reason: "css_gghud press clicks through the server; nothing moves the hidden client's mouse, whose CS2 never has focus (HARNESS.md). vote-my-vote shows the names at the foot of a tile, which need no hover.");
        R("sync-looks", "SERVER > Sync in its other looks: running with a progress bar, waiting, stopped, old or no word from the sync, a background download", section: SWindow, status: Not, today: "the real sync's own status (SyncDash): page-sync shows whichever it is in at that moment",
          reason: "Only the sync's status file sets them. No verb fakes one, and writing that file is state on production's volume.");
        R("loading-dot", "The Live dot gold while a new map's announcement is pending", section: SBoard, status: Not, today: "the stylesheet's .gg-global.loading .gg-live-dot, off AnnouncePending",
          reason: "It stands from a change until the map is announced, nearly all of it while the client is on its loading screen; no verb holds it.");
        return rows;
    }
}
