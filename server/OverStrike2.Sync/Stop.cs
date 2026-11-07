using System.Text.Json.Nodes;

namespace OverStrike2.Sync;

/// <summary>Why a step of a run ended early: its words, for the report, the history and the status file, and its kind, which the problem mail
/// goes by (Issues.Of) - never by the words, so a stop reworded is mailed as before.</summary>
public sealed record Stop(string Kind, string Words)
{
    public const string Routine = "routine";                     // the run's deadline, somebody on the server, the game updated: no problem
    public const string Pushback = "pushback";                   // Steam pushed back: no more downloads today
    public const string InARow = "in-a-row";                     // 3 downloads in a row failed
    public const string SteamCmd = "steamcmd";                   // SteamCMD did not log in, or stopped answering
    public const string Stalls = "stalls";                       // SteamCMD's second stall in the run (the mail goes by the run's stalls)
    public const string HandOversWaiting = "handovers-waiting";  // the game server has not taken in what waits for it
    public const string TestEngine = "test-engine";              // the test engine did not start
    public const string TestTree = "test-tree";                  // the test tree could not be built

    /// <summary>The stop, into a step's part of the report: its words as "stop", its kind as "stop_kind".</summary>
    public void Into(JsonObject step)
    {
        step["stop"] = Words;
        step["stop_kind"] = Kind;
    }
}
